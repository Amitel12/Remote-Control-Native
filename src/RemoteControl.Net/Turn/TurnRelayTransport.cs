using System.Diagnostics;
using System.Net;
using RemoteControl.Common;
using RemoteControl.Net.Transport;

namespace RemoteControl.Net.Turn;

/// <summary>
/// Carries the ordinary media stream over a TURN allocation, as an
/// <see cref="IUdpTransport"/> so that nothing above it changes: the LAN
/// session code, FEC, packetizer, congestion control and input path all run
/// unmodified over the relay, exactly as the P2P path already reuses them
/// over a hole-punched socket. Sends are wrapped in Send indications
/// addressed to the peer; inbound Data indications are unwrapped back into
/// plain datagrams.
///
/// It also keeps the allocation alive, which is not optional: TURN
/// permissions expire after five minutes and the allocation after its
/// lifetime, and when a permission lapses the server does not report
/// anything -- it silently drops the peer's packets, which upstream would
/// look like a total loss of video with no error anywhere. So both are
/// re-sent periodically from this class, driven off the same calls that move
/// media rather than a background timer, since there is no separate thread
/// here that owns the socket.
///
/// Overhead is 36 bytes per datagram (20-byte STUN header, 12-byte
/// XOR-PEER-ADDRESS, 4-byte DATA header). <see cref="MaxPayloadOverhead"/>
/// exists so the packetizer's MTU budget can account for it.
/// </summary>
public sealed class TurnRelayTransport : IUdpTransport
{
    /// <summary>Bytes each relayed datagram costs over sending it directly.</summary>
    public const int MaxPayloadOverhead = 36;


    private readonly IUdpTransport _inner;
    private readonly TurnClient _client;
    private readonly IPEndPoint _peer;
    private readonly IReadOnlyList<IPEndPoint> _permittedPeers;
    private readonly ILogger _logger;
    private readonly TimeSpan _keepAliveInterval;
    private readonly Stopwatch _sinceKeepAlive = Stopwatch.StartNew();
    private readonly byte[] _receiveBuffer = new byte[65535];
    // Media that Poll had to read in order to find out it *was* media. Poll cannot un-read a
    // datagram, so anything it uncovers waits here for the Receive that follows.
    private readonly Queue<byte[]> _pendingPayloads = new();

    public TurnRelayTransport(
        IUdpTransport inner,
        TurnClient client,
        IPEndPoint peer,
        IReadOnlyList<IPEndPoint>? permittedPeers = null,
        TimeSpan? keepAliveInterval = null,
        ILogger? logger = null)
    {
        // Two minutes by default: permissions lapse at five, so this leaves room for a couple of
        // lost requests before the relay starts silently dropping the peer. Overridable so tests
        // can exercise the keep-alive path without waiting minutes for it.
        _keepAliveInterval = keepAliveInterval ?? TimeSpan.FromMinutes(2);
        _inner = inner;
        _client = client;
        _peer = peer;
        // Every address the peer might reach us from needs its own permission -- they are keyed
        // on the peer's IP, and the peer may send from its relay or its server-reflexive address
        // depending on which side allocated.
        _permittedPeers = permittedPeers ?? [peer];
        _logger = logger ?? new ConsoleLogger(nameof(TurnRelayTransport));
        _inner.Connect(client.ServerEndpoint);
    }

    public int Available => _inner.Available;

    public EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    /// <summary>The peer address the relay is addressed to -- for logging; nothing here dials it directly.</summary>
    public IPEndPoint PeerEndpoint => _peer;

    public void Bind(IPEndPoint local) => _inner.Bind(local);

    /// <summary>
    /// Ignored: this transport is already pointed at the TURN server, and the peer it relays to
    /// was fixed at construction. Accepting a different remote here would silently send media
    /// somewhere it cannot arrive.
    /// </summary>
    public void Connect(IPEndPoint remote)
    {
        if (!remote.Equals(_peer))
            _logger.Warn($"Ignoring Connect({remote}) on a relayed transport pinned to {_peer}.");
    }

    public void Send(ReadOnlySpan<byte> datagram)
    {
        MaintainAllocation();
        _inner.Send(_client.BuildSendIndication(_peer, datagram));
    }

    public void SendTo(ReadOnlySpan<byte> datagram, EndPoint remote)
    {
        // Everything goes through the relay regardless of the address asked for: on this path
        // there is no route to the peer that does not.
        Send(datagram);
    }

    public int Receive(Span<byte> buffer)
    {
        if (_pendingPayloads.Count > 0) return CopyOut(_pendingPayloads.Dequeue(), buffer);

        while (true)
        {
            var received = _inner.Receive(_receiveBuffer);
            if (TryExtractPayload(received, out var payload)) return CopyOut(payload!, buffer);
        }
    }

    public int ReceiveFrom(Span<byte> buffer, ref EndPoint remote)
    {
        var length = Receive(buffer);
        remote = _peer;
        return length;
    }

    /// <summary>
    /// True only when a *media* datagram is ready -- which means consuming relay housekeeping
    /// here rather than reporting it as readable. Reporting it would be worse than untidy: the
    /// caller's loop treats a true as "a datagram is waiting" and calls Receive, which would
    /// then skip the housekeeping and block for media that may never come. The LAN client's
    /// own no-video timeout lives in its `!Poll` branch, so it would never run -- a stalled
    /// relay would hang the client instead of being reported, at exactly the moment the
    /// timeout exists for.
    /// </summary>
    public bool Poll(int microsecondsTimeout)
    {
        MaintainAllocation();
        if (_pendingPayloads.Count > 0) return true;

        var deadline = Stopwatch.GetTimestamp() + (long)(microsecondsTimeout / 1_000_000.0 * Stopwatch.Frequency);
        while (true)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            var remaining = remainingTicks <= 0
                ? 0
                : (int)Math.Min(int.MaxValue, remainingTicks * 1_000_000L / Stopwatch.Frequency);

            if (!_inner.Poll(remaining)) return false;

            var received = _inner.Receive(_receiveBuffer);
            if (TryExtractPayload(received, out var payload))
            {
                _pendingPayloads.Enqueue(payload!);
                return true;
            }
            // Housekeeping consumed; keep looking within whatever budget is left.
        }
    }

    /// <summary>
    /// Re-sends the allocation refresh and the peer permissions when they are due. Fire and
    /// forget by design -- the replies come back through <see cref="Receive"/> like anything
    /// else on this socket and are absorbed by <see cref="TurnClient.TryHandleResponse"/>.
    /// Waiting for them here would mean blocking the media path on the relay's housekeeping.
    /// </summary>
    private void MaintainAllocation()
    {
        if (_sinceKeepAlive.Elapsed < _keepAliveInterval) return;
        _sinceKeepAlive.Restart();

        try
        {
            _inner.Send(_client.BuildAuthenticatedRequest(TurnMethod.Refresh, [TurnMessage.BuildLifetime(600)]));
            foreach (var peer in _permittedPeers)
            {
                _inner.Send(_client.BuildAuthenticatedRequest(
                    TurnMethod.CreatePermission,
                    [TurnMessage.BuildXorAddress(TurnMessage.XorPeerAddressAttribute, peer)]));
            }
        }
        catch (Exception ex)
        {
            // Never let housekeeping kill a live stream: the allocation still has minutes left
            // when this runs, and the next attempt is two minutes away.
            _logger.Warn($"TURN keep-alive failed (allocation still valid for now): {ex.Message}");
        }
    }

    /// <summary>Separates media from relay housekeeping, absorbing the latter.</summary>
    private bool TryExtractPayload(int receivedLength, out byte[]? payload)
    {
        var datagram = _receiveBuffer.AsSpan(0, receivedLength);
        if (TurnClient.TryReadDataIndication(datagram, out _, out payload) && payload is not null)
            return true;

        // A Refresh/CreatePermission reply, or something else entirely -- either way not media.
        _client.TryHandleResponse(datagram);
        return false;
    }

    private int CopyOut(byte[] payload, Span<byte> buffer)
    {
        if (payload.Length > buffer.Length)
        {
            _logger.Warn($"Truncating a {payload.Length}-byte relayed datagram: the caller's buffer holds {buffer.Length}.");
            payload.AsSpan(0, buffer.Length).CopyTo(buffer);
            return buffer.Length;
        }

        payload.CopyTo(buffer);
        return payload.Length;
    }

    public void Dispose() => _inner.Dispose();
}
