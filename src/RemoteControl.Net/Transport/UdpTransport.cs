using System.Net;
using System.Net.Sockets;

namespace RemoteControl.Net.Transport;

/// <summary>Raw-socket <see cref="IUdpTransport"/> -- the only implementation for now; see IUdpTransport's remarks.</summary>
public sealed class UdpTransport : IUdpTransport
{
    private readonly Socket _socket;

    /// <summary>Buffer sizes &lt;= 0 leave that side at the OS default, matching the original inline Socket usage.</summary>
    public UdpTransport(int receiveBufferSize = 0, int sendBufferSize = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (receiveBufferSize > 0)
            _socket.ReceiveBufferSize = receiveBufferSize;
        if (sendBufferSize > 0)
            _socket.SendBufferSize = sendBufferSize;
    }

    public int Available => _socket.Available;

    public EndPoint? LocalEndPoint => _socket.LocalEndPoint;

    public void Bind(IPEndPoint local) => _socket.Bind(local);

    public void Connect(IPEndPoint remote) => _socket.Connect(remote);

    public void Send(ReadOnlySpan<byte> datagram) => _socket.Send(datagram);

    public void SendTo(ReadOnlySpan<byte> datagram, EndPoint remote) => _socket.SendTo(datagram, remote);

    public int Receive(Span<byte> buffer) => _socket.Receive(buffer);

    public int ReceiveFrom(Span<byte> buffer, ref EndPoint remote) => _socket.ReceiveFrom(buffer, ref remote);

    public bool Poll(int microsecondsTimeout) => _socket.Poll(microsecondsTimeout, SelectMode.SelectRead);

    public void Dispose() => _socket.Dispose();
}
