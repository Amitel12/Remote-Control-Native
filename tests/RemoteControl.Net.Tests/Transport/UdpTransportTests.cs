using System.Net;
using System.Text;
using RemoteControl.Net.Transport;
using Xunit;

namespace RemoteControl.Net.Tests.Transport;

public class UdpTransportTests
{
    [Fact]
    public void ConnectedSendReceive_RoundTrips_OverRealLoopbackSocket()
    {
        using IUdpTransport receiver = new UdpTransport();
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var receiverPort = ((IPEndPoint)receiver.LocalEndPoint!).Port;

        using IUdpTransport sender = new UdpTransport();
        sender.Connect(new IPEndPoint(IPAddress.Loopback, receiverPort));

        var payload = Encoding.UTF8.GetBytes("udp transport round trip");
        sender.Send(payload);

        Assert.True(receiver.Poll(5_000_000), "expected a datagram within 5s.");
        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        var buffer = new byte[256];
        var received = receiver.ReceiveFrom(buffer, ref source);

        Assert.Equal(payload, buffer[..received]);
    }

    [Fact]
    public void SendToThenReceive_RoundTrips_BackToConnectedSender()
    {
        using IUdpTransport client = new UdpTransport();
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var clientPort = ((IPEndPoint)client.LocalEndPoint!).Port;

        using IUdpTransport host = new UdpTransport();
        host.Connect(new IPEndPoint(IPAddress.Loopback, clientPort));
        host.Send(Encoding.UTF8.GetBytes("hello"));

        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        var helloBuffer = new byte[64];
        client.ReceiveFrom(helloBuffer, ref source);

        var reply = Encoding.UTF8.GetBytes("ack");
        client.SendTo(reply, source);

        Assert.True(host.Poll(5_000_000), "expected the reply within 5s.");
        var replyBuffer = new byte[64];
        var received = host.Receive(replyBuffer);
        Assert.Equal(reply, replyBuffer[..received]);
    }

    [Fact]
    public void Poll_ReturnsFalse_WhenNothingArrives()
    {
        using IUdpTransport transport = new UdpTransport();
        transport.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        Assert.False(transport.Poll(10_000)); // 10ms, nothing sent.
        Assert.Equal(0, transport.Available);
    }
}
