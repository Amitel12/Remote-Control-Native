using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteControl.Net.Transport;
using RemoteControl.Net.Turn;
using Xunit;

namespace RemoteControl.Net.Tests.Turn;

/// <summary>
/// Real loopback sockets throughout, matching UdpTransportTests: the point of this class is
/// that everything above it -- FEC, packetizer, session framing, input -- cannot tell it apart
/// from a plain socket, and that only holds if the wrapping and unwrapping survive a real
/// send/receive rather than a mocked one.
/// </summary>
public class TurnRelayTransportTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("198.51.100.7"), 40000);

    [Fact]
    public async Task SendAndReceive_LookLikePlainDatagrams_ThroughTheRelay()
    {
        using var server = new FakeTurnServer
        {
            RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155),
            EchoRelayedTraffic = true,
        };
        server.Start();

        using var relay = await ConnectRelayAsync(server);

        var payload = Encoding.UTF8.GetBytes("a video shard, as far as the caller is concerned");
        relay.Send(payload);

        Assert.True(relay.Poll(5_000_000), "expected the echoed datagram within 5s.");
        var buffer = new byte[512];
        var received = relay.Receive(buffer);

        Assert.Equal(payload, buffer[..received]);
        Assert.Equal(1, server.SendIndications);
    }

    [Fact]
    public async Task Receive_SkipsRelayHousekeeping_RatherThanReturningItAsMedia()
    {
        // The keep-alive replies arrive on the same socket as the media. If they were handed up
        // as datagrams, the depacketizer would see a STUN header where a video shard should be.
        using var server = new FakeTurnServer
        {
            RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155),
            EchoRelayedTraffic = true,
        };
        server.Start();

        // Zero interval: every send re-sends the refresh and permission, so the replies are
        // guaranteed to be racing the echoed media rather than needing a two-minute wait.
        using var relay = await ConnectRelayAsync(server, keepAliveInterval: TimeSpan.Zero);

        var payload = Encoding.UTF8.GetBytes("still just a datagram");
        relay.Send(payload);

        var buffer = new byte[512];
        var received = relay.Receive(buffer);

        Assert.Equal(payload, buffer[..received]);
        Assert.True(server.RefreshRequests >= 1, "expected the allocation refresh to have been sent.");
        Assert.True(server.PermissionRequests >= 1, "expected the peer permission to have been renewed.");
    }

    [Fact]
    public async Task Send_KeepsThePermissionAlive_ForEveryAddressThePeerMightUse()
    {
        // Permissions are per peer IP and expire after five minutes, silently -- the server
        // just stops relaying. A peer that switches between its relayed and reflexive address
        // must not fall off the permitted list.
        using var server = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155) };
        server.Start();

        var alternate = new IPEndPoint(IPAddress.Parse("198.51.100.8"), 41000);
        using var relay = await ConnectRelayAsync(server, keepAliveInterval: TimeSpan.Zero, permittedPeers: [Peer, alternate]);

        relay.Send(Encoding.UTF8.GetBytes("first"));
        await WaitForAsync(() => server.PermissionRequests >= 2);

        Assert.True(server.RefreshRequests >= 1);
        Assert.True(server.PermissionRequests >= 2);
    }

    [Fact]
    public async Task Connect_IsIgnored_RatherThanSilentlyRepointingTheStream()
    {
        // The LAN session code calls Connect on the transport it is handed. On the relay path
        // the destination is already fixed, and quietly honouring a different one would send
        // media to an address the relay cannot deliver to.
        using var server = new FakeTurnServer
        {
            RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155),
            EchoRelayedTraffic = true,
        };
        server.Start();

        using var relay = await ConnectRelayAsync(server);
        relay.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.55"), 5555));

        var payload = Encoding.UTF8.GetBytes("goes to the peer, not to 192.0.2.55");
        relay.Send(payload);

        var buffer = new byte[512];
        Assert.True(relay.Poll(5_000_000));
        Assert.Equal(payload, buffer[..relay.Receive(buffer)]);
        Assert.Equal(Peer, relay.PeerEndpoint);
    }

    [Fact]
    public async Task Poll_ReportsFalse_WhenOnlyRelayHousekeepingIsWaiting()
    {
        // Regression test for a hang, not a tidiness issue. The LAN client's no-video timeout
        // lives in its `if (!Poll(...))` branch, so a Poll that returns true for a Refresh reply
        // sends it into ReceiveFrom, which skips the housekeeping and then blocks for media that
        // is not coming. The client would hang forever at precisely the moment its timeout
        // exists to report the stall.
        using var server = new FakeTurnServer { RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155) };
        server.Start();

        // Zero interval: the next call sends a Refresh and a CreatePermission, so their replies
        // are the only thing that will ever arrive -- no media at all.
        using var relay = await ConnectRelayAsync(server, keepAliveInterval: TimeSpan.Zero);
        relay.Send(Encoding.UTF8.GetBytes("this goes to a peer that never answers"));
        await WaitForAsync(() => server.RefreshRequests >= 1);

        Assert.False(relay.Poll(500_000), "housekeeping replies must not be reported as readable media.");
    }

    [Fact]
    public async Task Poll_StillReportsMedia_ThatItHadToReadToIdentify()
    {
        // The other half of the same fix: Poll cannot un-read a datagram, so media it uncovers
        // while skipping housekeeping has to be held for the Receive that follows rather than
        // dropped.
        using var server = new FakeTurnServer
        {
            RelayedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 49155),
            EchoRelayedTraffic = true,
        };
        server.Start();

        using var relay = await ConnectRelayAsync(server, keepAliveInterval: TimeSpan.Zero);
        var payload = Encoding.UTF8.GetBytes("media behind two housekeeping replies");
        relay.Send(payload);

        Assert.True(relay.Poll(5_000_000), "expected the echoed media to be found behind the housekeeping.");
        var buffer = new byte[512];
        Assert.Equal(payload, buffer[..relay.Receive(buffer)]);
    }

    private static async Task<TurnRelayTransport> ConnectRelayAsync(
        FakeTurnServer server, TimeSpan? keepAliveInterval = null, IReadOnlyList<IPEndPoint>? permittedPeers = null)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new TurnClient(socket, server.Endpoint, "app-user", "s3cret");
        await client.AllocateAsync();

        // The allocation is done through the raw socket; the transport takes it over from here,
        // which is exactly the handover the P2P path performs after a successful punch.
        return new TurnRelayTransport(new UdpTransport(socket), client, Peer, permittedPeers, keepAliveInterval);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
    }
}
