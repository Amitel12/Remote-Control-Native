using System.Net;

namespace RemoteControl.Net.Transport;

/// <summary>
/// Seam between the LAN video path and the concrete socket it rides on --
/// docs/PHASE-1.md gate item 4. The Phase 1 video baseline stays on
/// <see cref="UdpTransport"/>: it already carries its own Reed-Solomon FEC
/// (see RemoteControl.Net.Fec), so ENet's reliable channels wouldn't add
/// anything here, and its unreliable channel is just UDP with extra framing.
/// ENet is expected to earn its place at Phase 3, when the input/control
/// channel needs real reliable delivery -- that implementation can target
/// this same interface without touching the video path.
/// </summary>
public interface IUdpTransport : IDisposable
{
    /// <summary>Number of bytes available to read without blocking (mirrors Socket.Available).</summary>
    int Available { get; }

    EndPoint? LocalEndPoint { get; }

    void Bind(IPEndPoint local);

    /// <summary>Fixes the remote endpoint for subsequent <see cref="Send"/>/<see cref="Receive"/> calls.</summary>
    void Connect(IPEndPoint remote);

    /// <summary>Sends to the endpoint set by <see cref="Connect"/>.</summary>
    void Send(ReadOnlySpan<byte> datagram);

    void SendTo(ReadOnlySpan<byte> datagram, EndPoint remote);

    /// <summary>Blocking receive from the connected endpoint; returns the byte count written into <paramref name="buffer"/>.</summary>
    int Receive(Span<byte> buffer);

    int ReceiveFrom(Span<byte> buffer, ref EndPoint remote);

    /// <summary>True once a datagram is available to read, or the timeout elapses.</summary>
    bool Poll(int microsecondsTimeout);
}
