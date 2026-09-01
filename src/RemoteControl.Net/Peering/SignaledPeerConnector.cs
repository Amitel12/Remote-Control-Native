using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteControl.Common;
using RemoteControl.Net.Stun;
using RemoteControl.Protocol;

namespace RemoteControl.Net.Peering;

/// <summary>
/// Automates the candidate exchange that Phase 2 has so far done by hand:
/// register with the signaling server, trade STUN candidates with the peer
/// over it, and hole-punch to what comes back -- returning a peer endpoint
/// the caller can stream to exactly as if it had been typed in manually.
///
/// This closes the gap docs/PHASE-2.md calls "What's still manual / open":
/// <see cref="StunClient"/> and <see cref="HolePunchCoordinator"/> both
/// existed and worked, and <c>SignalingClient</c> spoke the protocol, but
/// nothing connected the three. Manual copy/paste was not just tedious --
/// it caused most of the failed punch attempts recorded in that document
/// (candidates gone stale after a restart, a transcribed typo, one side's
/// punch window elapsing before the other side had finished starting up).
/// Every one of those failure modes is specific to a human moving the
/// candidates; none of them survive this.
///
/// Deliberately takes the same <see cref="Socket"/> throughout, and hands
/// it back still bound to the same local port: NATs key their translations
/// on (local address, port), so discovery, punching, and the media stream
/// that follows must all be the same socket or the mapping the punch opened
/// does not apply to the traffic that matters.
/// </summary>
public sealed class SignaledPeerConnector
{
    private readonly ISignalingChannel _channel;
    private readonly Socket _socket;
    private readonly ILogger _logger;

    // Set once the local candidates exist, so the peer-joined handler below can wait
    // for them rather than racing STUN discovery.
    private readonly TaskCompletionSource<IReadOnlyList<CandidateInit>> _localCandidates =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IReadOnlyList<CandidateInit>> _peerCandidates =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SignaledPeerConnector(ISignalingChannel channel, Socket socket, ILogger? logger = null)
    {
        _channel = channel;
        _socket = socket;
        _logger = logger ?? new ConsoleLogger(nameof(SignaledPeerConnector));
    }

    /// <summary>
    /// Connects, registers under <paramref name="pairingCode"/>, exchanges
    /// candidates with the peer, and punches. Returns the endpoint a probe
    /// was actually received from -- which is the one to stream to, and may
    /// differ from any advertised candidate if a NAT rewrote it.
    /// </summary>
    /// <param name="stunServer">
    /// Where to discover this socket's server-reflexive candidate. Null
    /// advertises host candidates only, which is all two peers on one LAN
    /// need (and is what the tests use, since a unit test should not depend
    /// on a public STUN server being up).
    /// </param>
    /// <param name="localAddresses">
    /// Which local addresses to advertise as host candidates. Defaults to
    /// every operational non-loopback IPv4 address. Loopback is excluded by
    /// default on purpose: a probe sent to 127.0.0.1 travels to whatever is
    /// on that port *on the prober's own machine*, so advertising it to a
    /// remote peer is at best useless and at worst a false "path is open".
    /// Tests running both peers in one process pass it explicitly.
    /// </param>
    public async Task<IPEndPoint> ConnectAsync(
        Role role,
        string pairingCode,
        IPEndPoint? stunServer = null,
        TimeSpan? punchTimeout = null,
        IReadOnlyList<IPAddress>? localAddresses = null,
        TimeSpan? registrationTimeout = null,
        CancellationToken cancellationToken = default)
    {
        _channel.MessageReceived += OnMessageReceived;
        _channel.Closed += OnClosed;
        try
        {
            await _channel.ConnectAsync(cancellationToken);
            await _channel.SendAsync(new ClientMessage.Register(role, pairingCode), cancellationToken);
            var registrationBudget = registrationTimeout ?? TimeSpan.FromSeconds(30);
            try
            {
                await _registered.Task.WaitAsync(registrationBudget, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"The signaling server did not acknowledge registration within {registrationBudget}. " +
                    "It is reachable (the WebSocket connected) but is not answering 'register'.");
            }
            _logger.Info($"Registered with the signaling server as {role} under pairing code {pairingCode}.");

            var local = await GatherCandidatesAsync(stunServer, localAddresses, cancellationToken);
            _localCandidates.TrySetResult(local);
            await SendCandidatesAsync(local, cancellationToken);

            // No timeout here, deliberately: what we are waiting for is a *person* starting
            // the other side, which can take as long as it takes. The same call
            // docs/PHASE-1.md's LAN handshake made ("wait indefinitely"), for the same
            // reason -- a timeout here just converts "they were slow" into a confusing
            // failure. Cancellation (Ctrl+C) is the way out.
            var peer = await _peerCandidates.Task.WaitAsync(cancellationToken);
            var peerEndpoints = ToEndpoints(peer);
            if (peerEndpoints.Count == 0)
                throw new InvalidOperationException("The peer advertised no usable IPv4 candidates.");

            // Sent before punching rather than after: it tells the peer probes are on their
            // way, and the peer's own punch is what makes ours land. Purely informational --
            // we do not wait for theirs before starting, since both sides reach this point
            // within a round trip of each other and gating on it would add a deadlock for
            // no benefit.
            await _channel.SendAsync(new ClientMessage.HolePunchReady(), cancellationToken);

            var timeout = punchTimeout ?? TimeSpan.FromSeconds(30);
            _logger.Info($"Punching toward {string.Join(", ", peerEndpoints)} (up to {timeout.TotalSeconds:0}s).");
            var coordinator = new HolePunchCoordinator(_socket, _logger);
            var established = await coordinator.PunchAsync(peerEndpoints, timeout, cancellationToken: cancellationToken);
            if (established is null)
            {
                throw new TimeoutException(
                    $"Hole punch to {string.Join(", ", peerEndpoints)} did not succeed within {timeout}. " +
                    "This is the restrictive-NAT case TURN relay fallback exists for (docs/PHASE-2.md).");
            }

            _logger.Info($"Path open to {established}.");
            return established;
        }
        finally
        {
            _channel.MessageReceived -= OnMessageReceived;
            _channel.Closed -= OnClosed;
            // A late peer-left/error can fault a step nothing is waiting on any more (we may
            // have already failed on an earlier one, or succeeded outright) -- observe both so
            // that never surfaces as an unobserved task exception.
            Observe(_registered.Task);
            Observe(_peerCandidates.Task);
            Observe(_localCandidates.Task);
        }
    }

    private void OnMessageReceived(ServerMessage message)
    {
        switch (message)
        {
            case ServerMessage.Registered:
                _registered.TrySetResult();
                break;

            case ServerMessage.PeerJoined:
                // The peer registered *after* we did, so it never saw the candidates we sent on
                // registration -- the server relays to whoever is in the room at the time, it does
                // not replay. Without this resend the first peer to arrive is the one side that
                // never learns the other's candidates, and both sides sit waiting.
                _logger.Info("Peer joined the room -- re-sending our candidates.");
                _ = ResendCandidatesAsync();
                break;

            case ServerMessage.StunCandidates candidates:
                _logger.Info($"Peer candidates received: {string.Join(", ", candidates.Candidates.Select(Describe))}.");
                _peerCandidates.TrySetResult(candidates.Candidates);
                break;

            case ServerMessage.HolePunchReady:
                _logger.Info("Peer reports it has started punching.");
                break;

            case ServerMessage.PeerLeft:
                // Only fatal while we are still waiting on them. Once punching has started the
                // path no longer depends on the signaling connection at all, and a peer that
                // closes its WebSocket after the exchange is a normal thing to do.
                Fail(new InvalidOperationException("The peer left the room before exchanging candidates."));
                break;

            case ServerMessage.Error error:
                Fail(new InvalidOperationException($"Signaling server rejected the session ({error.Code}): {error.Message}"));
                break;
        }
    }

    private void OnClosed() =>
        Fail(new InvalidOperationException("The signaling connection closed before the candidate exchange completed."));

    /// <summary>
    /// Faults whichever handshake step is still outstanding. A no-op once both have
    /// completed, so a connection dropping after the exchange -- which is harmless, the
    /// punched socket does not need it any more -- cannot fail an established session.
    /// </summary>
    private void Fail(Exception exception)
    {
        _registered.TrySetException(exception);
        _peerCandidates.TrySetException(exception);
        // Also releases a peer-joined re-send still waiting on candidates that will now never
        // be gathered, rather than leaving that continuation parked for the connector's life.
        _localCandidates.TrySetException(exception);
    }

    private async Task ResendCandidatesAsync()
    {
        try
        {
            // Awaits rather than reads: peer-joined can arrive while STUN discovery is still
            // running, in which case the candidates to resend do not exist yet.
            var candidates = await _localCandidates.Task;
            await SendCandidatesAsync(candidates, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from an event handler, so nothing else would ever observe this.
            _logger.Warn($"Re-sending candidates after peer-joined failed: {ex.Message}");
        }
    }

    private Task SendCandidatesAsync(IReadOnlyList<CandidateInit> candidates, CancellationToken cancellationToken)
    {
        _logger.Info($"Advertising candidates: {string.Join(", ", candidates.Select(Describe))}.");
        return _channel.SendAsync(new ClientMessage.StunCandidates(candidates), cancellationToken);
    }

    private async Task<IReadOnlyList<CandidateInit>> GatherCandidatesAsync(
        IPEndPoint? stunServer, IReadOnlyList<IPAddress>? localAddresses, CancellationToken cancellationToken)
    {
        var port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        var candidates = (localAddresses ?? EnumerateLocalIPv4Addresses())
            .Select(address => new CandidateInit(CandidateKind.Host, address.ToString(), port))
            .ToList();

        if (stunServer is not null)
        {
            var reflexive = await new StunClient(_socket, _logger).DiscoverReflexiveEndpointAsync(
                stunServer, cancellationToken: cancellationToken);
            if (reflexive is not null)
                candidates.Add(new CandidateInit(CandidateKind.Srflx, reflexive.Address.ToString(), reflexive.Port));
            else
                _logger.Warn($"STUN discovery against {stunServer} got no answer -- advertising host candidates only.");
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("No local candidates to advertise: no usable IPv4 address and no STUN result.");

        return candidates;
    }

    private static IReadOnlyList<IPAddress> EnumerateLocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                          && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToList();

    /// <summary>
    /// Relay candidates are accepted and punched at like any other -- a TURN allocation is
    /// just another address as far as this is concerned -- but nothing allocates one yet, so
    /// in practice they never appear. See docs/PHASE-2.md.
    /// </summary>
    private static IReadOnlyList<IPEndPoint> ToEndpoints(IReadOnlyList<CandidateInit> candidates)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (var candidate in candidates)
        {
            if (!IPAddress.TryParse(candidate.Ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (candidate.Port is <= 0 or > 65535)
                continue;

            var endpoint = new IPEndPoint(address, candidate.Port);
            if (!endpoints.Contains(endpoint)) // a peer can advertise the same address twice (host == srflx behind no NAT).
                endpoints.Add(endpoint);
        }

        return endpoints;
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string Describe(CandidateInit candidate) =>
        $"{candidate.Kind.ToString().ToLowerInvariant()} {candidate.Ip}:{candidate.Port}";
}
