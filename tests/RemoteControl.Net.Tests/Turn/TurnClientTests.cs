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
}
