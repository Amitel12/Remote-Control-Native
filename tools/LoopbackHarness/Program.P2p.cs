using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;
using RemoteControl.Net.Stun;
using RemoteControl.Net.Transport;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 2 -- NAT traversal (docs/ARCHITECTURE.md's phased build order,
/// item 3). No deployed signaling server to exchange candidates through
/// yet, so this stands in for it with a manual copy/paste: each side
/// discovers its own STUN candidate, prints it, and reads the other side's
/// off stdin. Once <see cref="HolePunchCoordinator"/> opens the path, this
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
        ILogger logger, int localPort, IPEndPoint stunServer, IPEndPoint? remoteCandidate,
        int targetFrames, int parityPercent, int dropPercent, bool adaptiveBitrate, bool adaptiveFec, bool intraRefresh, bool remoteInput)
    {
        using var rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = LanReceiveBufferSize,
            SendBufferSize = LanReceiveBufferSize,
        };
        rawSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        var peer = DiscoverAndPunchAsync(logger, rawSocket, stunServer, remoteCandidate).GetAwaiter().GetResult();
        IUdpTransport socket = new UdpTransport(rawSocket);
        socket.Connect(peer);
        RunLanHostWithTransport(logger, socket, $"{peer} (P2P, hole-punched)", targetFrames, parityPercent, dropPercent, adaptiveBitrate, adaptiveFec, intraRefresh, remoteInput);
    }

    private static void RunP2pClient(
        ILogger logger, int localPort, IPEndPoint stunServer, IPEndPoint? remoteCandidate, int targetFrames, bool verifyFrame, bool remoteInput, int dropInputPercent)
    {
        using var rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = LanReceiveBufferSize,
        };
        rawSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        DiscoverAndPunchAsync(logger, rawSocket, stunServer, remoteCandidate).GetAwaiter().GetResult();
        IUdpTransport socket = new UdpTransport(rawSocket);
        logger.Info("Peer reachable -- entering the normal LAN client session (video streaming is identical either way).");
        RunLanClientSession(logger, socket, targetFrames, verifyFrame, remoteInput, dropInputPercent);
    }

    /// <summary>
    /// Runs STUN discovery, then hole-punches to <paramref name="remoteCandidate"/>
    /// if supplied (non-interactive -- for driving this from another
    /// process/tool), otherwise prompts for it on stdin (for a human at a
    /// real terminal). Returns the confirmed-reachable peer endpoint.
    /// </summary>
    private static async Task<IPEndPoint> DiscoverAndPunchAsync(
        ILogger logger, Socket socket, IPEndPoint stunServer, IPEndPoint? remoteCandidate)
    {
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
}
