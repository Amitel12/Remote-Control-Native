using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RemoteControl.Net.Turn;
using Xunit;

namespace RemoteControl.Net.Tests.Turn;

/// <summary>
/// Drives <see cref="TurnClient"/> against a fake TURN server that is a real UDP socket
/// speaking the real message format -- the same approach StunClientTests takes, and for the
/// same reason: the interesting failures are in the exchange (the 401 challenge that is
/// supposed to happen, a rotated nonce, a rejected permission), not in any one message.
/// </summary>
public class TurnClientTests
{
    private const string Username = "app-user";
    private const string Password = "s3cret";
    private const string Realm = "remote-control.local";

    [Fact]
    public async Task AllocateAsync_AnswersThe401Challenge_AndReturnsTheRelayedAddress()
    {
        var relayed = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155);
        using var server = new FakeTurnServer { RelayedEndpoint = relayed };
        server.Start();

        using var socket = BindLoopbackSocket();
        var client = new TurnClient(socket, server.Endpoint, Username, Password);

        var allocation = await client.AllocateAsync();

        Assert.Equal(relayed, allocation.RelayedEndpoint);
        Assert.Equal(600u, allocation.LifetimeSeconds);
        // The rejection is the handshake: an unauthenticated Allocate first, then a second one
        // carrying the credentials the 401 asked for.
        Assert.Equal(2, server.AllocateRequests);
        Assert.False(server.FirstAllocateWasAuthenticated);
        Assert.True(server.LastAllocateWasAuthenticated);
    }

    [Fact]
    public async Task AllocateAsync_ReplaysTheRequest_WhenTheServerRotatesItsNonce()
    {
        // coturn expires nonces on its own schedule, so a 438 is routine rather than a failure
        // -- if it were surfaced to callers, an allocation would randomly fail in production.
        using var server = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155), StaleNonceOnce = true };
        server.Start();

        using var socket = BindLoopbackSocket();
        var client = new TurnClient(socket, server.Endpoint, Username, Password);

        var allocation = await client.AllocateAsync();

        Assert.Equal(49155, allocation.RelayedEndpoint.Port);
        Assert.Equal(3, server.AllocateRequests); // unauthenticated, stale-nonce, then accepted
        Assert.Equal("nonce-two", server.LastNonceSeen);
    }

    [Fact]
    public async Task AllocateAsync_Throws_WhenTheServerRejectsTheCredentials()
    {
        using var server = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155), RejectAuthenticatedWith = 401 };
        server.Start();

        using var socket = BindLoopbackSocket();
        var client = new TurnClient(socket, server.Endpoint, Username, "wrong-password");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AllocateAsync());
        Assert.Contains("401", failure.Message);
    }

    [Fact]
    public async Task CreatePermissionAsync_Throws_WhenTheServerRefusesThePeer()
    {
        using var server = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155), PermissionError = 403 };
        server.Start();

        using var socket = BindLoopbackSocket();
        var client = new TurnClient(socket, server.Endpoint, Username, Password);
        await client.AllocateAsync();

        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePermissionAsync(peer));
        Assert.Contains("403", failure.Message);
    }

    [Fact]
    public async Task AllocateAsync_Throws_WhenTheServerNeverAnswers()
    {
        using var socket = BindLoopbackSocket();
        // Nothing is listening there, so every retransmission goes nowhere.
        var client = new TurnClient(socket, new IPEndPoint(IPAddress.Loopback, 1), Username, Password);

        await Assert.ThrowsAsync<TimeoutException>(() => client.AllocateAsync());
    }

    [Fact]
    public void SendAndDataIndications_CarryThePeerAddressAndPayload()
    {
        using var socket = BindLoopbackSocket();
        var client = new TurnClient(socket, new IPEndPoint(IPAddress.Loopback, 3478), Username, Password);
        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01];

        var send = client.BuildSendIndication(peer, payload);
        Assert.True(TurnMessage.TryParse(send, out var parsedSend));
        Assert.Equal(TurnMethod.Send, parsedSend.Method);
        Assert.Equal(StunClass.Indication, parsedSend.Class);
        Assert.Equal(peer, parsedSend.PeerEndpoint);
        Assert.Equal(payload, parsedSend.Data);

        // What the server sends back is a Data indication with the same shape.
        var data = TurnMessage.Build(
            TurnMethod.Data, StunClass.Indication, TurnMessage.NewTransactionId(),
            [
                TurnMessage.BuildXorAddress(TurnMessage.XorPeerAddressAttribute, peer),
                TurnMessage.BuildAttribute(TurnMessage.DataAttribute, payload),
            ]);

        Assert.True(TurnClient.TryReadDataIndication(data, out var readPeer, out var readPayload));
        Assert.Equal(peer, readPeer);
        Assert.Equal(payload, readPayload);

        // A video shard is not a Data indication and must not be mistaken for one.
        Assert.False(TurnClient.TryReadDataIndication([0x01, 0x02, 0x03, 0x04], out _, out _));
        Assert.False(TurnClient.TryReadDataIndication(send, out _, out _)); // Send, not Data
    }

    private static Socket BindLoopbackSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private sealed class FakeTurnServer : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _cts = new();
        private int _authenticatedAllocates;

        public FakeTurnServer() => _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;
        public required IPEndPoint RelayedEndpoint { get; init; }

        /// <summary>Answer the first authenticated Allocate with 438 Stale Nonce and a fresh nonce.</summary>
        public bool StaleNonceOnce { get; init; }

        /// <summary>Reject every authenticated Allocate with this error code instead of allocating.</summary>
        public int? RejectAuthenticatedWith { get; init; }

        /// <summary>Answer CreatePermission with this error code instead of success.</summary>
        public int? PermissionError { get; init; }

        public int AllocateRequests { get; private set; }
        public bool FirstAllocateWasAuthenticated { get; private set; }
        public bool LastAllocateWasAuthenticated { get; private set; }
        public string? LastNonceSeen { get; private set; }

        public void Start() => _ = Task.Run(RunAsync);

        private async Task RunAsync()
        {
            var buffer = new byte[2048];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var received = await _socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                    var datagram = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                    if (!TurnMessage.TryParse(datagram, out var request)) continue;

                    var reply = BuildReply(datagram, request);
                    if (reply is not null)
                        await _socket.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, _cts.Token);
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
            catch (ObjectDisposedException) { /* disposed */ }
        }

        private byte[]? BuildReply(byte[] datagram, TurnParsedMessage request)
        {
            var authenticated = HasAttribute(datagram, TurnMessage.UsernameAttribute)
                                && HasAttribute(datagram, TurnMessage.MessageIntegrityAttribute);

            switch (request.Method)
            {
                case TurnMethod.Allocate:
                    AllocateRequests++;
                    if (AllocateRequests == 1) FirstAllocateWasAuthenticated = authenticated;
                    LastAllocateWasAuthenticated = authenticated;
                    if (request.Nonce is not null) LastNonceSeen = request.Nonce;

                    if (!authenticated)
                        return Challenge(request, 401, "nonce-one");

                    if (StaleNonceOnce && ++_authenticatedAllocates == 1)
                        return Challenge(request, 438, "nonce-two");

                    if (RejectAuthenticatedWith is { } rejection)
                        return Challenge(request, rejection, "nonce-one");

                    return TurnMessage.Build(
                        TurnMethod.Allocate, StunClass.SuccessResponse, request.TransactionId,
                        [
                            TurnMessage.BuildXorAddress(TurnMessage.XorRelayedAddressAttribute, RelayedEndpoint),
                            TurnMessage.BuildLifetime(600),
                        ]);

                case TurnMethod.CreatePermission:
                    return PermissionError is { } permissionError
                        ? Challenge(request, permissionError, "nonce-one")
                        : TurnMessage.Build(TurnMethod.CreatePermission, StunClass.SuccessResponse, request.TransactionId, []);

                case TurnMethod.Refresh:
                    return TurnMessage.Build(
                        TurnMethod.Refresh, StunClass.SuccessResponse, request.TransactionId, [TurnMessage.BuildLifetime(600)]);

                default:
                    return null;
            }
        }

        private static byte[] Challenge(TurnParsedMessage request, int errorCode, string nonce)
        {
            var value = new byte[] { 0, 0, (byte)(errorCode / 100), (byte)(errorCode % 100) };
            return TurnMessage.Build(
                request.Method, StunClass.ErrorResponse, request.TransactionId,
                [
                    TurnMessage.BuildAttribute(TurnMessage.ErrorCodeAttribute, value),
                    TurnMessage.BuildStringAttribute(TurnMessage.RealmAttribute, Realm),
                    TurnMessage.BuildStringAttribute(TurnMessage.NonceAttribute, nonce),
                ]);
        }

        /// <summary>Walks the attribute list looking for one type -- the client's own parser doesn't surface USERNAME or MESSAGE-INTEGRITY, and this needs to see that they were actually sent.</summary>
        private static bool HasAttribute(byte[] message, ushort wanted)
        {
            var attributesLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
            var offset = 20;
            var end = Math.Min(message.Length, 20 + attributesLength);
            while (offset + 4 <= end)
            {
                var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2));
                if (type == wanted) return true;
                offset += 4 + ((length + 3) & ~3);
            }
            return false;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _socket.Dispose();
            _cts.Dispose();
        }
    }
}
