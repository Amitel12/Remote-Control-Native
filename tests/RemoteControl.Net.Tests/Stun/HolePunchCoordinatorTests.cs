using System.Net;
using System.Net.Sockets;
using RemoteControl.Net.Stun;
using Xunit;

namespace RemoteControl.Net.Tests.Stun;

public class HolePunchCoordinatorTests
{
    [Fact]
    public async Task PunchAsync_Succeeds_WhenBothSidesProbeEachOther_OverRealLoopbackSockets()
    {
        using var socketA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socketA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var socketB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socketB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var endpointA = (IPEndPoint)socketA.LocalEndPoint!;
        var endpointB = (IPEndPoint)socketB.LocalEndPoint!;

        var coordinatorA = new HolePunchCoordinator(socketA);
        var coordinatorB = new HolePunchCoordinator(socketB);

        var timeout = TimeSpan.FromSeconds(5);
        var taskA = coordinatorA.PunchAsync([endpointB], timeout);
        var taskB = coordinatorB.PunchAsync([endpointA], timeout);
        await Task.WhenAll(taskA, taskB);

        Assert.Equal(endpointB, await taskA);
        Assert.Equal(endpointA, await taskB);
    }

    [Fact]
    public async Task PunchAsync_ReturnsNull_WhenNothingResponds()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var coordinator = new HolePunchCoordinator(socket);

        // Nothing is listening on this port -- probes go nowhere, no reply ever arrives.
        var deadEndpoint = new IPEndPoint(IPAddress.Loopback, 1);
        var result = await coordinator.PunchAsync([deadEndpoint], TimeSpan.FromMilliseconds(500));

        Assert.Null(result);
    }
}
