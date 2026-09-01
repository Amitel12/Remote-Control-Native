using System.Net;
using System.Net.Sockets;
using RemoteControl.Net.Peering;
using RemoteControl.Net.Tests.Turn;
using RemoteControl.Net.Turn;
using RemoteControl.Protocol;
using Xunit;

namespace RemoteControl.Net.Tests.Peering;

/// <summary>
/// Exercises the full candidate exchange against a fake in-process signaling
/// server that relays exactly like the real one does (rooms.ts: acknowledge
/// the sender's register, tell whoever was already in the room that a peer
/// joined, forward everything else to the other side) -- and against real
/// UDP sockets doing a real hole punch over loopback, the same approach
/// StunClientTests and HolePunchCoordinatorTests already take.
///
/// What's being tested is the choreography, which is where the failure modes
/// are: candidates sent before the peer exists are never seen by anyone, and
/// the first peer into the room is the one that has to notice and re-send.
/// </summary>
public class SignaledPeerConnectorTests
{
    private static readonly IPAddress[] LoopbackOnly = [IPAddress.Loopback];

    [Fact]
    public async Task ConnectAsync_ExchangesCandidatesAndPunches_WhenTheHostRegistersFirst()
    {
        var server = new FakeSignalingServer();
        using var hostSocket = BindLoopbackSocket();
        using var clientSocket = BindLoopbackSocket();

        // The host registers into an empty room, so the candidates it sends on registration
        // reach nobody -- only the peer-joined re-send can rescue this, which is the point.
        var hostConnect = new SignaledPeerConnector(server.CreateChannel(), hostSocket).ConnectAsync(
            Role.Host, "ABC123", punchTimeout: TimeSpan.FromSeconds(5), localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);

        var clientConnect = new SignaledPeerConnector(server.CreateChannel(), clientSocket).ConnectAsync(
            Role.Client, "ABC123", punchTimeout: TimeSpan.FromSeconds(5), localAddresses: LoopbackOnly);

        var established = await Task.WhenAll(hostConnect, clientConnect);

        Assert.Equal((IPEndPoint)clientSocket.LocalEndPoint!, established[0].PeerEndpoint);
        Assert.Equal((IPEndPoint)hostSocket.LocalEndPoint!, established[1].PeerEndpoint);
        Assert.All(established, connection => Assert.False(connection.ViaRelay));
    }

    [Fact]
    public async Task ConnectAsync_Punches_WhenBothSidesAreAlreadyInTheRoom()
    {
        // The other ordering: both register before either sends candidates, so the initial
        // send is the one that lands and the re-send is redundant. Both orderings have to
        // work -- which side registers first is a race in real use.
        var server = new FakeSignalingServer();
        using var hostSocket = BindLoopbackSocket();
        using var clientSocket = BindLoopbackSocket();
        var hostChannel = server.CreateChannel();
        var clientChannel = server.CreateChannel();

        var hostConnect = new SignaledPeerConnector(hostChannel, hostSocket).ConnectAsync(
            Role.Host, "ABC123", punchTimeout: TimeSpan.FromSeconds(5), localAddresses: LoopbackOnly);
        var clientConnect = new SignaledPeerConnector(clientChannel, clientSocket).ConnectAsync(
            Role.Client, "ABC123", punchTimeout: TimeSpan.FromSeconds(5), localAddresses: LoopbackOnly);

        var established = await Task.WhenAll(hostConnect, clientConnect);

        Assert.Equal((IPEndPoint)clientSocket.LocalEndPoint!, established[0].PeerEndpoint);
        Assert.Equal((IPEndPoint)hostSocket.LocalEndPoint!, established[1].PeerEndpoint);
        Assert.All(established, connection => Assert.False(connection.ViaRelay));
    }

    [Fact]
    public async Task ConnectAsync_Throws_WhenTheServerRejectsRegistration()
    {
        var server = new FakeSignalingServer { RejectRegistrationWith = new ServerMessage.Error("register-failed", "room is full") };
        using var socket = BindLoopbackSocket();

        var connector = new SignaledPeerConnector(server.CreateChannel(), socket);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ConnectAsync(
            Role.Client, "ABC123", localAddresses: LoopbackOnly));

        Assert.Contains("register-failed", failure.Message);
        Assert.Contains("room is full", failure.Message);
    }

    [Fact]
    public async Task ConnectAsync_Throws_WhenThePeerLeavesBeforeSendingCandidates()
    {
        var server = new FakeSignalingServer();
        using var socket = BindLoopbackSocket();
        var channel = server.CreateChannel();

        var connect = new SignaledPeerConnector(channel, socket).ConnectAsync(
            Role.Host, "ABC123", localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);
        server.DeliverTo(channel, new ServerMessage.PeerLeft());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
        Assert.Contains("left the room", failure.Message);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsTimeout_WhenThePeerAdvertisesAnUnreachableCandidate()
    {
        var server = new FakeSignalingServer();
        using var socket = BindLoopbackSocket();
        var channel = server.CreateChannel();

        var connect = new SignaledPeerConnector(channel, socket).ConnectAsync(
            Role.Host, "ABC123", punchTimeout: TimeSpan.FromMilliseconds(500), localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);
        // Nothing is listening there, so probes go out and nothing ever comes back -- the
        // restrictive-NAT shape of failure, which must surface as a clear error rather than
        // hanging forever.
        server.DeliverTo(channel, new ServerMessage.StunCandidates([new CandidateInit(CandidateKind.Srflx, "127.0.0.1", 1)]));

        await Assert.ThrowsAsync<TimeoutException>(() => connect);
    }

    [Fact]
    public async Task ConnectAsync_Throws_WhenThePeerAdvertisesNothingUsable()
    {
        var server = new FakeSignalingServer();
        using var socket = BindLoopbackSocket();
        var channel = server.CreateChannel();

        var connect = new SignaledPeerConnector(channel, socket).ConnectAsync(
            Role.Host, "ABC123", localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);
        // IPv6 and a garbage address: both parse-or-skip paths, leaving nothing to punch at.
        server.DeliverTo(channel, new ServerMessage.StunCandidates([
            new CandidateInit(CandidateKind.Host, "::1", 5000),
            new CandidateInit(CandidateKind.Host, "not-an-ip", 5000),
        ]));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
        Assert.Contains("no usable IPv4 candidates", failure.Message);
    }

    [Fact]
    public async Task ConnectAsync_NeverOverlapsSends_WhenPeerJoinArrivesAroundTheInitialSend()
    {
        // Regression test for a real defect: completing the local-candidates source releases the
        // parked peer-joined re-send onto the thread pool, and the statement right after it began
        // the initial send -- two sends in flight on a transport that allows one. The earlier
        // fake completed sends instantly and so could never catch it; this one holds each send
        // open long enough for an overlap to be observable, which is what a real WebSocket does.
        var server = new FakeSignalingServer { SendDuration = TimeSpan.FromMilliseconds(150) };
        using var socket = BindLoopbackSocket();
        var channel = server.CreateChannel();

        var connect = new SignaledPeerConnector(channel, socket).ConnectAsync(
            Role.Host, "ABC123", punchTimeout: TimeSpan.FromMilliseconds(200), localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);

        // peer-joined lands around the initial send (triggering the re-send), and the candidates
        // that follow push the connector straight into its hole-punch-ready send -- three writes
        // with every opportunity to overlap.
        server.DeliverTo(channel, new ServerMessage.PeerJoined());
        server.DeliverTo(channel, new ServerMessage.StunCandidates([new CandidateInit(CandidateKind.Srflx, "127.0.0.1", 1)]));

        await Assert.ThrowsAsync<TimeoutException>(() => connect);
        Assert.False(server.SawConcurrentSend, "The connector issued two sends at once; a real ClientWebSocket would have thrown.");
    }

    [Fact]
    public async Task ConnectAsync_FallsBackToTheRelay_WhenThePunchTimesOut()
    {
        // The case docs/PHASE-2.md hit for real: a clean punch attempt that still times out. Up
        // to now that was the end of the road; the relay is what makes such a network usable.
        using var turnServer = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155) };
        turnServer.Start();
        var server = new FakeSignalingServer();
        using var socket = BindLoopbackSocket();
        var channel = server.CreateChannel();

        var connect = new SignaledPeerConnector(channel, socket).ConnectAsync(
            Role.Host, "ABC123",
            turn: new TurnCredentials(turnServer.Endpoint, "app-user", "s3cret"),
            punchTimeout: TimeSpan.FromMilliseconds(300),
            localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);

        var peerRelay = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 49200);
        server.DeliverTo(channel, new ServerMessage.StunCandidates([
            new CandidateInit(CandidateKind.Srflx, "127.0.0.1", 1), // nothing there: the punch fails
            new CandidateInit(CandidateKind.Relay, peerRelay.Address.ToString(), peerRelay.Port),
        ]));

        var result = await connect;
        using var transport = result.Transport;

        Assert.True(result.ViaRelay);
        Assert.Equal(peerRelay, result.PeerEndpoint);
        Assert.IsType<TurnRelayTransport>(result.Transport);
        // The relay only forwards for peers it has been told about, so the fallback has to
        // create permissions before handing back a transport.
        Assert.True(turnServer.PermissionRequests >= 1, "expected a permission for the peer's relayed address.");
    }

    [Fact]
    public async Task ConnectAsync_AdvertisesARelayCandidate_WhenTurnIsConfigured()
    {
        // The allocation has to happen during gathering: candidates are exchanged once, so a
        // relayed address obtained later could never reach the peer.
        using var turnServer = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155) };
        turnServer.Start();
        var server = new FakeSignalingServer();
        using var socket = BindLoopbackSocket();
        var channel = server.CreateChannel();

        var connect = new SignaledPeerConnector(channel, socket).ConnectAsync(
            Role.Host, "ABC123",
            turn: new TurnCredentials(turnServer.Endpoint, "app-user", "s3cret"),
            punchTimeout: TimeSpan.FromMilliseconds(200),
            localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);
        await WaitForAsync(() => server.LastCandidatesFrom(channel) is not null);

        var advertised = server.LastCandidatesFrom(channel)!;
        Assert.Contains(advertised, candidate => candidate.Kind == CandidateKind.Relay
                                                 && candidate.Ip == "203.0.113.9" && candidate.Port == 49155);

        // The peer has no relay of its own, but we do -- so the fallback relays to their
        // reflexive address, which works because their NAT opens for our relay once they are
        // sending to it. One-sided TURN is enough.
        var peerReflexive = new IPEndPoint(IPAddress.Loopback, 1);
        server.DeliverTo(channel, new ServerMessage.StunCandidates([
            new CandidateInit(CandidateKind.Srflx, peerReflexive.Address.ToString(), peerReflexive.Port),
        ]));

        var result = await connect;
        using var transport = result.Transport;

        Assert.True(result.ViaRelay);
        Assert.Equal(peerReflexive, result.PeerEndpoint);
    }

    [Fact]
    public async Task ConnectAsync_ContinuesWithoutARelay_WhenTheTurnServerIsUnreachable()
    {
        // A relay that cannot be allocated is a worse connection, not a failed one -- the direct
        // path may still work, and refusing to continue would turn degraded into down.
        var server = new FakeSignalingServer();
        using var hostSocket = BindLoopbackSocket();
        using var clientSocket = BindLoopbackSocket();

        var unreachableTurn = new TurnCredentials(new IPEndPoint(IPAddress.Loopback, 1), "app-user", "s3cret");
        var hostConnect = new SignaledPeerConnector(server.CreateChannel(), hostSocket).ConnectAsync(
            Role.Host, "ABC123", turn: unreachableTurn, punchTimeout: TimeSpan.FromSeconds(5), localAddresses: LoopbackOnly);
        await server.WaitForMemberCountAsync(1);
        var clientConnect = new SignaledPeerConnector(server.CreateChannel(), clientSocket).ConnectAsync(
            Role.Client, "ABC123", turn: unreachableTurn, punchTimeout: TimeSpan.FromSeconds(5), localAddresses: LoopbackOnly);

        var established = await Task.WhenAll(hostConnect, clientConnect);

        Assert.All(established, connection => Assert.False(connection.ViaRelay));
        Assert.Equal((IPEndPoint)clientSocket.LocalEndPoint!, established[0].PeerEndpoint);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++) await Task.Delay(10);
    }

    private static Socket BindLoopbackSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    /// <summary>
    /// Models packages/signaling-server's relay behaviour: register is acknowledged to the
    /// sender and announced to whoever was already in the room, and every other message is
    /// forwarded to the other members verbatim. Notably it does *not* tell a joining peer
    /// that someone was already there, and does not replay anything sent before it arrived --
    /// the two properties the re-send logic exists for.
    /// </summary>
    private sealed class FakeSignalingServer
    {
        private readonly List<FakeChannel> _members = [];
        private readonly Dictionary<FakeChannel, IReadOnlyList<CandidateInit>> _advertised = new();
        private readonly object _gate = new();

        public ServerMessage? RejectRegistrationWith { get; init; }

        /// <summary>
        /// How long a send stays in flight. A real WebSocket send is not instant; leaving it at
        /// zero is what let the first version of these tests miss a genuine overlapping-send bug.
        /// </summary>
        public TimeSpan SendDuration { get; init; } = TimeSpan.Zero;

        /// <summary>
        /// Set if any channel ever had two sends in flight at once. A real
        /// <c>ClientWebSocket</c> allows exactly one and throws on the second, so this is a
        /// defect in the caller, not a tolerable race.
        /// </summary>
        public bool SawConcurrentSend { get; private set; }

        private void NoteConcurrentSend() => SawConcurrentSend = true;

        public ISignalingChannel CreateChannel() => new FakeChannel(this);

        public void DeliverTo(ISignalingChannel channel, ServerMessage message) => ((FakeChannel)channel).Deliver(message);

        /// <summary>The candidates a peer last sent, so a test can check what actually went on the wire.</summary>
        public IReadOnlyList<CandidateInit>? LastCandidatesFrom(ISignalingChannel channel)
        {
            lock (_gate) return _advertised.GetValueOrDefault((FakeChannel)channel);
        }

        public async Task WaitForMemberCountAsync(int count)
        {
            // Registration crosses a queue, so the count lags the caller's own await points.
            for (var attempt = 0; attempt < 200; attempt++)
            {
                lock (_gate)
                {
                    if (_members.Count >= count) return;
                }
                await Task.Delay(10);
            }

            throw new TimeoutException($"Only {_members.Count} member(s) registered, expected {count}.");
        }

        private void OnSent(FakeChannel sender, ClientMessage message)
        {
            switch (message)
            {
                case ClientMessage.Register register:
                    if (RejectRegistrationWith is not null)
                    {
                        sender.Deliver(RejectRegistrationWith);
                        return;
                    }

                    List<FakeChannel> existing;
                    lock (_gate)
                    {
                        existing = [.. _members];
                        _members.Add(sender);
                    }

                    sender.Deliver(new ServerMessage.Registered(register.PairingCode, register.Role));
                    foreach (var member in existing) member.Deliver(new ServerMessage.PeerJoined());
                    break;

                case ClientMessage.StunCandidates candidates:
                    lock (_gate) _advertised[sender] = candidates.Candidates;
                    Relay(sender, new ServerMessage.StunCandidates(candidates.Candidates));
                    break;

                case ClientMessage.HolePunchReady:
                    Relay(sender, new ServerMessage.HolePunchReady());
                    break;
            }
        }

        private void Relay(FakeChannel sender, ServerMessage message)
        {
            List<FakeChannel> peers;
            lock (_gate)
            {
                peers = _members.Where(member => member != sender).ToList();
            }

            foreach (var peer in peers) peer.Deliver(message);
        }

        private sealed class FakeChannel : ISignalingChannel
        {
            private readonly FakeSignalingServer _server;
            private readonly object _queueGate = new();
            private Task _delivery = Task.CompletedTask;
            private int _inFlightSends;

            public FakeChannel(FakeSignalingServer server) => _server = server;

            public event Action<ServerMessage>? MessageReceived;
            public event Action? Closed;

            public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async Task SendAsync(ClientMessage message, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref _inFlightSends) > 1)
                    _server.NoteConcurrentSend();
                try
                {
                    if (_server.SendDuration > TimeSpan.Zero)
                        await Task.Delay(_server.SendDuration, cancellationToken);
                    _server.OnSent(this, message);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlightSends);
                }
            }

            /// <summary>
            /// Queued rather than invoked inline, so a handler that sends in response can't
            /// re-enter the sender on its own stack -- the real client raises these from its
            /// WebSocket receive loop. Chained so per-connection order still holds.
            /// </summary>
            public void Deliver(ServerMessage message)
            {
                lock (_queueGate)
                {
                    _delivery = _delivery.ContinueWith(
                        _ => MessageReceived?.Invoke(message),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
            }

            public ValueTask DisposeAsync()
            {
                Closed?.Invoke();
                return ValueTask.CompletedTask;
            }
        }
    }
}
