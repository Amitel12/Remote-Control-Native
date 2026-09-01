using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteControl.Common;
using RemoteControl.Net.Stun;
using RemoteControl.Net.Transport;
using RemoteControl.Net.Turn;
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
/// <summary>Where to reach the TURN server and how to authenticate -- coturn's lt-cred-mech user.</summary>
public sealed record TurnCredentials(IPEndPoint Server, string Username, string Password);

/// <summary>
/// A peer connection ready to stream over, however it was reached. Callers get a transport
/// rather than an endpoint precisely because the relay case cannot be expressed as one: relayed
/// media is addressed to the TURN server and wrapped, so "the peer's address" is not something
/// you can simply send to.
///
/// The transport is handed over unconnected on the direct path, matching what the P2P harness
/// has always done -- the host half connects, the client half uses SendTo/ReceiveFrom. Calling
/// Connect on a relayed transport is harmless: it is already pointed at the relay, and says so.
/// </summary>
public sealed record PeerConnection(IUdpTransport Transport, IPEndPoint PeerEndpoint, bool ViaRelay)
{
    public string Describe() => ViaRelay ? $"{PeerEndpoint} (TURN relay)" : $"{PeerEndpoint} (P2P, hole-punched)";
}

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
    // Every write to the channel goes through this. Completing _localCandidates releases the
    // peer-joined re-send onto the thread pool, and the very next statement starts the initial
    // send -- two sends in flight at once on a transport that allows one. A real ClientWebSocket
    // throws InvalidOperationException on the second, and if the re-send got there first it is
    // the *initial* send that throws, failing an exchange that was otherwise fine.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    // Set during candidate gathering when TURN is configured, so the fallback below already has
    // an allocation to use -- it cannot be obtained after the punch fails, because the relayed
    // address has to be advertised in the same candidate exchange the peer already consumed.
    private TurnClient? _turnClient;
    private uint _allocationLifetimeSeconds;

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
    public async Task<PeerConnection> ConnectAsync(
        Role role,
        string pairingCode,
        IPEndPoint? stunServer = null,
        TurnCredentials? turn = null,
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
            await SendAsync(new ClientMessage.Register(role, pairingCode), cancellationToken);
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

            var local = await GatherCandidatesAsync(stunServer, turn, localAddresses, cancellationToken);
            _localCandidates.TrySetResult(local);
            await SendCandidatesAsync(local, cancellationToken);

            // No timeout here, deliberately: what we are waiting for is a *person* starting
            // the other side, which can take as long as it takes. The same call
            // docs/PHASE-1.md's LAN handshake made ("wait indefinitely"), for the same
            // reason -- a timeout here just converts "they were slow" into a confusing
            // failure. Cancellation (Ctrl+C) is the way out.
            // That indefinite wait outlives the allocation: coturn grants ten minutes, and a peer
            // that takes longer than that to start would leave us advertising a relayed address
            // that no longer exists -- the fallback would then fail with Allocation Mismatch
            // rather than connecting, which is a confusing way to discover the relay expired.
            // So the allocation is kept alive for as long as the wait lasts.
            IReadOnlyList<CandidateInit> peer;
            using (var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var keepingAlive = KeepAllocationAliveAsync(keepAlive.Token);
                try
                {
                    peer = await _peerCandidates.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    // Awaited, not just cancelled: a refresh in flight owns the socket, and the
                    // punch that follows needs it to itself.
                    keepAlive.Cancel();
                    await keepingAlive;
                }
            }
            // Relay candidates are excluded from punching on purpose: a relayed address is the
            // TURN server, which will not answer a punch probe and does not need to -- it is
            // reached by relaying, below.
            var peerEndpoints = ToEndpoints(peer, includeRelay: false);
            var peerHasRelay = FirstEndpointOfKind(peer, CandidateKind.Relay) is not null;
            if (peerEndpoints.Count == 0 && !peerHasRelay)
                throw new InvalidOperationException("The peer advertised no usable IPv4 candidates.");

            // Sent before punching rather than after: it tells the peer probes are on their
            // way, and the peer's own punch is what makes ours land. Purely informational --
            // we do not wait for theirs before starting, since both sides reach this point
            // within a round trip of each other and gating on it would add a deadlock for
            // no benefit.
            await SendAsync(new ClientMessage.HolePunchReady(), cancellationToken);

            var timeout = punchTimeout ?? TimeSpan.FromSeconds(30);
            if (peerEndpoints.Count == 0)
            {
                _logger.Info("The peer advertised only a relayed address -- nothing to punch at, going straight to the relay.");
                return await FallBackToRelayAsync(peer, timeout, cancellationToken);
            }

            _logger.Info($"Punching toward {string.Join(", ", peerEndpoints)} (up to {timeout.TotalSeconds:0}s).");
            var coordinator = new HolePunchCoordinator(_socket, _logger);
            var established = await coordinator.PunchAsync(peerEndpoints, timeout, cancellationToken: cancellationToken);
            if (established is not null)
            {
                _logger.Info($"Path open to {established} -- direct, no relay.");
                // Deliberately not connected here: the LAN client half drives this socket with
                // SendTo/ReceiveFrom and learns the host's address from the first datagram, and
                // connecting a UDP socket out from under that is exactly the kind of change that
                // works on one OS and not the other. Whoever wants a connected socket -- the
                // host half does -- calls Connect itself, as it always has.
                return new PeerConnection(new UdpTransport(_socket), established, ViaRelay: false);
            }

            _logger.Warn($"Hole punch did not succeed within {timeout} -- this is the restrictive-NAT case (docs/PHASE-2.md).");
            return await FallBackToRelayAsync(peer, timeout, cancellationToken);
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
        return SendAsync(new ClientMessage.StunCandidates(candidates), cancellationToken);
    }

    /// <summary>
    /// One send at a time, no matter which of the connector's paths it comes from -- see
    /// <see cref="_sendGate"/>. Deliberately not left to the channel: a WebSocket permitting
    /// one outstanding send is the common case, not a quirk, so the caller not overlapping its
    /// own writes is the safer contract.
    /// </summary>
    private async Task SendAsync(ClientMessage message, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _channel.SendAsync(message, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<IReadOnlyList<CandidateInit>> GatherCandidatesAsync(
        IPEndPoint? stunServer, TurnCredentials? turn, IReadOnlyList<IPAddress>? localAddresses, CancellationToken cancellationToken)
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

        if (turn is not null)
        {
            // Allocated now rather than lazily on failure: the relayed address is only useful if
            // the peer learns it, and the exchange that carries candidates happens once.
            // A relay that turns out to be unnecessary costs one allocation the server drops on
            // its own when the lifetime runs out.
            try
            {
                var client = new TurnClient(_socket, turn.Server, turn.Username, turn.Password, _logger);
                var allocation = await client.AllocateAsync(cancellationToken);
                _turnClient = client;
                _allocationLifetimeSeconds = allocation.LifetimeSeconds;
                candidates.Add(new CandidateInit(CandidateKind.Relay, allocation.RelayedEndpoint.Address.ToString(), allocation.RelayedEndpoint.Port));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing relay is a worse connection, not a failed one -- the direct path may
                // still work, and refusing to continue would turn a degraded case into an outage.
                _logger.Warn($"TURN allocation against {turn.Server} failed, continuing without a relay candidate: {ex.Message}");
            }
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("No local candidates to advertise: no usable IPv4 address and no STUN result.");

        return candidates;
    }

    /// <summary>
    /// Refreshes the allocation for as long as we are waiting on the peer. Runs at half the
    /// granted lifetime, so a single lost refresh still leaves a full interval to recover in.
    /// Failures are logged rather than thrown: the direct path may still work, and a relay that
    /// has expired will announce itself clearly enough when the fallback tries to use it.
    /// </summary>
    private async Task KeepAllocationAliveAsync(CancellationToken cancellationToken)
    {
        if (_turnClient is null || _allocationLifetimeSeconds == 0) return;

        var interval = TimeSpan.FromSeconds(Math.Max(1, _allocationLifetimeSeconds / 2.0));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                var granted = await _turnClient.RefreshAsync(cancellationToken: cancellationToken);
                _logger.Info($"TURN allocation refreshed while waiting for the peer ({granted}s).");
            }
        }
        catch (OperationCanceledException)
        {
            // The peer arrived, or the caller gave up. Either way this is done.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Keeping the TURN allocation alive failed: {ex.Message}");
        }
    }

    /// <summary>
    /// What happens on the network docs/PHASE-2.md found: a clean punch attempt that still
    /// times out. Three cases, in order of preference -- our own allocation (relay to relay, or
    /// relay to the peer's reflexive address), the peer's allocation if only they have one
    /// (we send to it directly, which works because their permission covers us), or nothing,
    /// which is the honest failure this used to be in every case.
    /// </summary>
    private async Task<PeerConnection> FallBackToRelayAsync(
        IReadOnlyList<CandidateInit> peerCandidates, TimeSpan punchTimeout, CancellationToken cancellationToken)
    {
        var peerRelay = FirstEndpointOfKind(peerCandidates, CandidateKind.Relay);
        var peerReflexive = FirstEndpointOfKind(peerCandidates, CandidateKind.Srflx);

        if (_turnClient is not null)
        {
            var target = peerRelay ?? peerReflexive;
            if (target is null)
                throw new TimeoutException($"Hole punch failed within {punchTimeout} and the peer advertised no relayed or reflexive address to fall back to.");

            // Permit both: which address the peer's media actually arrives from depends on
            // whether they relayed too, and a missing permission is dropped silently by the
            // server rather than reported.
            var permitted = new List<IPEndPoint>();
            foreach (var candidate in new[] { peerRelay, peerReflexive })
                if (candidate is not null && !permitted.Contains(candidate)) permitted.Add(candidate);

            foreach (var peer in permitted)
                await _turnClient.CreatePermissionAsync(peer, cancellationToken);

            _logger.Info($"Falling back to the TURN relay: sending to {target} through {_turnClient.ServerEndpoint}.");
            var relayTransport = new TurnRelayTransport(new UdpTransport(_socket), _turnClient, target, permitted, logger: _logger);
            return new PeerConnection(relayTransport, target, ViaRelay: true);
        }

        if (peerRelay is not null)
        {
            // Only the peer has a relay. Their allocation is reachable directly, and their
            // permission for us is what makes the return path work -- so a plain socket pointed
            // at their relayed address is enough, no allocation of our own required.
            _logger.Info($"Falling back to the peer's TURN relay at {peerRelay} (no allocation of our own).");
            var transport = new UdpTransport(_socket);
            transport.Connect(peerRelay);
            return new PeerConnection(transport, peerRelay, ViaRelay: true);
        }

        throw new TimeoutException(
            $"Hole punch did not succeed within {punchTimeout} and no TURN relay is available on either side. " +
            "This is the restrictive-NAT case a relay exists for (docs/PHASE-2.md) -- configure one.");
    }

    private static IPEndPoint? FirstEndpointOfKind(IReadOnlyList<CandidateInit> candidates, CandidateKind kind)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Kind != kind) continue;
            if (IPAddress.TryParse(candidate.Ip, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                return new IPEndPoint(address, candidate.Port);
        }

        return null;
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

    /// <summary>Flattens candidates to endpoints, dropping anything not IPv4 and, for punching, relay addresses.</summary>
    private static IReadOnlyList<IPEndPoint> ToEndpoints(IReadOnlyList<CandidateInit> candidates, bool includeRelay)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (var candidate in candidates)
        {
            if (!includeRelay && candidate.Kind == CandidateKind.Relay) continue;
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
