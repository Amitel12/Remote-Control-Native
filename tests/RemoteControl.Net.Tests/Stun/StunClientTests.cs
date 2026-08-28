using System.Net;
using System.Net.Sockets;
using RemoteControl.Net.Stun;
using Xunit;

namespace RemoteControl.Net.Tests.Stun;

/// <summary>
/// Exercises StunClient against a real UDP loopback socket and a minimal
/// in-process fake STUN server (not a mock -- an actual socket receiving
/// and replying to real Binding Requests), so this is testing the real
/// send/receive/timeout/retry path, not just StunMessage's pure parsing
/// logic (already covered in StunMessageTests).
/// </summary>
public class StunClientTests
{
    [Fact]
    public async Task DiscoverReflexiveEndpointAsync_ReturnsServerReportedEndpoint()
    {
        using var fakeServer = new FakeStunServer(reportedEndpoint: new IPEndPoint(IPAddress.Parse("203.0.113.42"), 55555));
        var serverTask = fakeServer.RunOnceAsync();

        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new StunClient(clientSocket);

        var result = await client.DiscoverReflexiveEndpointAsync(fakeServer.Endpoint, attempts: 3, perAttemptTimeout: TimeSpan.FromSeconds(2));

        await serverTask;
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("203.0.113.42"), result!.Address);
        Assert.Equal(55555, result.Port);
    }

    [Fact]
    public async Task DiscoverReflexiveEndpointAsync_ReturnsNull_WhenServerNeverResponds()
    {
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new StunClient(clientSocket);

        // Nothing is listening on this port -- exercises the retry-then-give-up path
        // without needing a slow real test (short timeout/attempts keeps this fast).
        var unreachable = new IPEndPoint(IPAddress.Loopback, 1);
        var result = await client.DiscoverReflexiveEndpointAsync(unreachable, attempts: 2, perAttemptTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Null(result);
    }

    private sealed class FakeStunServer : IDisposable
    {
        private readonly Socket _socket;
        private readonly IPEndPoint _reportedEndpoint;

        public IPEndPoint Endpoint { get; }

        public FakeStunServer(IPEndPoint reportedEndpoint)
        {
            _reportedEndpoint = reportedEndpoint;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
        }

        public async Task RunOnceAsync()
        {
            var buffer = new byte[512];
            var receiveResult = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));

            // Real request parsing (not a shortcut): pull the transaction ID
            // straight out of the request bytes at its real header offset,
            // same as a real STUN server would, so the response really is a
            // reply keyed to this specific request.
            var transactionId = buffer.AsSpan(8, StunMessage.TransactionIdLength).ToArray();

            var response = BuildBindingResponse(transactionId, _reportedEndpoint);
            await _socket.SendToAsync(response, SocketFlags.None, receiveResult.RemoteEndPoint);
        }

        private static byte[] BuildBindingResponse(byte[] transactionId, IPEndPoint mapped)
        {
            const uint magicCookie = 0x2112A442;
            var cookieBytes = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
            var addressBytes = mapped.Address.GetAddressBytes();
            var xorPort = (ushort)(mapped.Port ^ (magicCookie >> 16));
            var xorAddress = new byte[4];
            for (var i = 0; i < 4; i++) xorAddress[i] = (byte)(addressBytes[i] ^ cookieBytes[i]);

            var message = new List<byte>();
            message.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x0c });
            message.AddRange(cookieBytes);
            message.AddRange(transactionId);
            message.AddRange(new byte[] { 0x00, 0x20, 0x00, 0x08, 0x00, 0x01, (byte)(xorPort >> 8), (byte)xorPort });
            message.AddRange(xorAddress);
            return message.ToArray();
        }

        public void Dispose() => _socket.Dispose();
    }
}
