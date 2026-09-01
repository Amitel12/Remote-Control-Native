using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;
using RemoteControl.Net.Peering;
using RemoteControl.Net.Stun;
using RemoteControl.Net.Transport;
using RemoteControl.Protocol;
using RemoteControl.Signaling;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 2 -- NAT traversal (docs/ARCHITECTURE.md's phased build order,
/// item 3). Two ways to exchange candidates: pass
/// <c>--signaling-server</c>/<c>--pairing-code</c> and
/// <see cref="SignaledPeerConnector"/> does it automatically over the real
/// signaling WebSocket, or omit them and each side discovers its own STUN
/// candidate, prints it, and reads the other side's off stdin. The manual
/// path is kept because it needs no deployed server -- it is how every
/// Phase 2 result so far was obtained -- but it is the fragile one: most of
/// the failed punch attempts in docs/PHASE-2.md were stale or mistyped
/// candidates, not NAT behaviour. Once <see cref="HolePunchCoordinator"/> opens the path, this
/// hands the exact same socket into the Phase 1 host/client session code
/// (<see cref="RunLanHostWithTransport"/>/<see cref="RunLanClientSession"/>)
/// -- streaming itself doesn't care whether the transport arrived via a
/// known LAN IP or a hole-punched NAT mapping.
/// </summary>
internal static partial class Program
{
    private static IPEndPoint ParseStunServer(string value)
    {
        var separatorIndex = value.LastIndexOf(':');
        if (separatorIndex < 0 || !int.TryParse(value[(separatorIndex + 1)..], out var port))
            throw new ArgumentException($"--stun-server requires host:port; got '{value}'.");

        var host = value[..separatorIndex];
        if (IPAddress.TryParse(host, out var literal))
            return new IPEndPoint(literal, port);

        var resolved = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (resolved is null)
            throw new ArgumentException($"--stun-server host '{host}' did not resolve to an IPv4 address.");
        return new IPEndPoint(resolved, port);
    }

    private static void RunP2pHost(
        ILogger logger, int localPort, IPEndPoint stunServer, IPEndPoint? remoteCandidate, SignalingOptions? signaling,
        int targetFrames, int parityPercent, int dropPercent, bool adaptiveBitrate, bool adaptiveFec, bool intraRefresh, bool remoteInput)
    {
        using var rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = LanReceiveBufferSize,
            SendBufferSize = LanReceiveBufferSize,
        };
        rawSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        var peer = DiscoverAndPunchAsync(logger, rawSocket, stunServer, remoteCandidate, signaling, Role.Host).GetAwaiter().GetResult();
        IUdpTransport socket = new UdpTransport(rawSocket);
        socket.Connect(peer);
        RunLanHostWithTransport(logger, socket, $"{peer} (P2P, hole-punched)", targetFrames, parityPercent, dropPercent, adaptiveBitrate, adaptiveFec, intraRefresh, remoteInput);
    }

    private static void RunP2pClient(
        ILogger logger, int localPort, IPEndPoint stunServer, IPEndPoint? remoteCandidate, SignalingOptions? signaling,
        int targetFrames, bool verifyFrame, bool remoteInput, int dropInputPercent)
    {
        using var rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = LanReceiveBufferSize,
        };
        rawSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        DiscoverAndPunchAsync(logger, rawSocket, stunServer, remoteCandidate, signaling, Role.Client).GetAwaiter().GetResult();
        IUdpTransport socket = new UdpTransport(rawSocket);
        logger.Info("Peer reachable -- entering the normal LAN client session (video streaming is identical either way).");
        RunLanClientSession(logger, socket, targetFrames, verifyFrame, remoteInput, dropInputPercent);
    }

    /// <summary>
    /// Gets the peer endpoint, by whichever route is available: the real
    /// signaling server when one was configured, otherwise STUN discovery
    /// plus a candidate supplied on the command line
    /// (<paramref name="remoteCandidate"/>, for driving this from another
    /// process) or typed at the prompt. Returns the confirmed-reachable peer
    /// endpoint either way -- the streaming code that follows cannot tell
    /// the difference.
    /// </summary>
    private static async Task<IPEndPoint> DiscoverAndPunchAsync(
        ILogger logger, Socket socket, IPEndPoint stunServer, IPEndPoint? remoteCandidate,
        SignalingOptions? signaling, Role role)
    {
        if (signaling is not null)
        {
            if (remoteCandidate is not null)
                logger.Warn("--remote-candidate is ignored when --signaling-server is set; candidates come from the peer.");
            return await ConnectViaSignalingAsync(logger, socket, stunServer, signaling, role);
        }

        var local = (IPEndPoint)socket.LocalEndPoint!;
        logger.Info($"Local candidate: {local} (only useful if the peer is on the same LAN).");

        var stunClient = new StunClient(socket, logger);
        var reflexive = await stunClient.DiscoverReflexiveEndpointAsync(stunServer);
        if (reflexive is null)
        {
            logger.Warn($"STUN discovery against {stunServer} got no answer -- only the local candidate above is available.");
        }
        else
        {
            logger.Info($"Server-reflexive candidate (give this to the other side): {reflexive}");
        }

        var peerCandidate = remoteCandidate;
        if (peerCandidate is null)
        {
            Console.Write("Paste the other side's candidate (ip:port) and press Enter: ");
            while (peerCandidate is null)
            {
                var line = Console.ReadLine();
                if (line is not null && IPEndPoint.TryParse(line.Trim(), out peerCandidate))
                    break;
                Console.Write("Could not parse that as ip:port -- try again: ");
            }
        }

        logger.Info($"Hole-punching toward {peerCandidate} (up to 30s)...");
        var coordinator = new HolePunchCoordinator(socket, logger);
        var established = await coordinator.PunchAsync([peerCandidate], TimeSpan.FromSeconds(30));
        if (established is null)
            throw new TimeoutException($"Hole punch to {peerCandidate} did not succeed within 30s.");

        logger.Info($"Hole punch succeeded -- path open to {established}.");
        return established;
    }

    /// <summary>
    /// The automated path: everything from registration to an open socket is
    /// handled by <see cref="SignaledPeerConnector"/>. The signaling
    /// connection is deliberately kept open only for the exchange -- once the
    /// punch lands, the media path is the punched UDP socket and nothing
    /// needs the WebSocket any more.
    /// </summary>
    private static async Task<IPEndPoint> ConnectViaSignalingAsync(
        ILogger logger, Socket socket, IPEndPoint stunServer, SignalingOptions signaling, Role role)
    {
        logger.Info($"Connecting to signaling server {signaling.ServerUri} as {role} (pairing code {signaling.PairingCode}).");
        await using var channel = new SignalingClient(signaling.ServerUri, logger);
        var connector = new SignaledPeerConnector(channel, socket, logger);
        return await connector.ConnectAsync(role, signaling.PairingCode, stunServer, TimeSpan.FromSeconds(30));
    }

    /// <summary>Both halves are required together -- a pairing code means nothing without a server to pair through.</summary>
    internal sealed record SignalingOptions(Uri ServerUri, string PairingCode);

    private static SignalingOptions? ReadSignalingOptions(string[] args)
    {
        var server = ReadOption(args, "--signaling-server");
        var pairingCode = ReadOption(args, "--pairing-code");
        if (server is null && pairingCode is null) return null;
        if (server is null || pairingCode is null)
            throw new ArgumentException("--signaling-server and --pairing-code must be given together.");
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new ArgumentException($"--signaling-server requires a ws:// or wss:// URL; got '{server}'.");

        return new SignalingOptions(uri, pairingCode);
    }
}
