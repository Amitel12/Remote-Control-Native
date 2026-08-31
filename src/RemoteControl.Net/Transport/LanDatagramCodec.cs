using System.Buffers.Binary;

namespace RemoteControl.Net.Transport;

public enum LanDatagramKind : byte
{
    Configuration = 1,
    Ready = 2,
    Video = 3,
    End = 4,
    LatencyProbe = 5,
    LatencyEcho = 6,
    QualityReport = 7,
    Input = 8,
}

public readonly record struct LanDatagram(
    LanDatagramKind Kind,
    ulong SessionId,
    uint Width,
    uint Height,
    uint FpsNumerator,
    uint FpsDenominator,
    ReadOnlyMemory<byte> Payload);

/// <summary>
/// Minimal Phase 1 LAN envelope around the existing video-shard payload.
/// The handshake guarantees the receiver has configured its decoder before
/// the sender emits the first H.264 IDR frame.
/// </summary>
public static class LanDatagramCodec
{
    private const uint Magic = 0x314E4352; // "RCN1" in little-endian bytes.
    private const int CommonHeaderSize = 13;
    private const int ConfigurationSize = CommonHeaderSize + 16;

    // Probe payload: [PerfTicks int64][WallTicks int64], both the host's own
    // clocks at send time. Echo payload adds the client's wall clock at
    // receipt, unchanged perf/wall ticks passed straight through -- the
    // client never has to interpret them, only echo them back verbatim.
    private const int LatencyProbePayloadSize = 16;
    private const int LatencyEchoPayloadSize = 24;
    private const int LatencyProbeSize = CommonHeaderSize + LatencyProbePayloadSize;
    private const int LatencyEchoSize = CommonHeaderSize + LatencyEchoPayloadSize;

    // QualityReport payload: [FrameLossRate float32], 0..1 -- the client's own windowed
    // fraction of frames that never fully reassembled even after FEC recovery. See
    // RemoteControl.Net.Congestion.CongestionController, the only consumer.
    private const int QualityReportPayloadSize = 4;
    private const int QualityReportSize = CommonHeaderSize + QualityReportPayloadSize;

    public static byte[] CreateConfiguration(
        ulong sessionId,
        uint width,
        uint height,
        uint fpsNumerator,
        uint fpsDenominator)
    {
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be non-zero.");
        if (fpsNumerator == 0 || fpsDenominator == 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNumerator), "Frame rate must be non-zero.");

        var datagram = CreateHeader(LanDatagramKind.Configuration, sessionId, ConfigurationSize);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(13, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(17, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(21, 4), fpsNumerator);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(25, 4), fpsDenominator);
        return datagram;
    }

    public static byte[] CreateReady(ulong sessionId) =>
        CreateHeader(LanDatagramKind.Ready, sessionId, CommonHeaderSize);

    public static byte[] CreateEnd(ulong sessionId) =>
        CreateHeader(LanDatagramKind.End, sessionId, CommonHeaderSize);

    public static byte[] WrapVideo(ulong sessionId, ReadOnlySpan<byte> videoPacket)
    {
        if (videoPacket.IsEmpty)
            throw new ArgumentException("Video packet must not be empty.", nameof(videoPacket));

        var datagram = CreateHeader(
            LanDatagramKind.Video,
            sessionId,
            checked(CommonHeaderSize + videoPacket.Length));
        videoPacket.CopyTo(datagram.AsSpan(CommonHeaderSize));
        return datagram;
    }

    /// <summary>
    /// Sent by the host, roughly once a second. <paramref name="perfTicks"/>
    /// (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>) is what the
    /// host uses to compute round-trip time when the echo comes back -- it
    /// never leaves the host's own clock domain, so no cross-machine clock
    /// sync is needed for RTT. <paramref name="wallTicks"/>
    /// (<see cref="DateTime.UtcNow"/>.Ticks) is only for the client-side
    /// clock-offset estimate in the echo.
    /// </summary>
    public static byte[] CreateLatencyProbe(ulong sessionId, long perfTicks, long wallTicks)
    {
        var datagram = CreateHeader(LanDatagramKind.LatencyProbe, sessionId, LatencyProbeSize);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(CommonHeaderSize, 8), perfTicks);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(CommonHeaderSize + 8, 8), wallTicks);
        return datagram;
    }

    /// <summary>
    /// Sent by the client immediately on receiving a probe. Passes the
    /// probe's own two fields straight through unexamined, and appends the
    /// client's wall clock at receipt so the host can estimate clock offset.
    /// </summary>
    public static byte[] CreateLatencyEcho(ulong sessionId, long probePerfTicks, long probeWallTicks, long clientWallTicks)
    {
        var datagram = CreateHeader(LanDatagramKind.LatencyEcho, sessionId, LatencyEchoSize);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(CommonHeaderSize, 8), probePerfTicks);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(CommonHeaderSize + 8, 8), probeWallTicks);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(CommonHeaderSize + 16, 8), clientWallTicks);
        return datagram;
    }

    /// <summary>Reads a <see cref="LanDatagramKind.LatencyProbe"/> datagram's payload.</summary>
    public static (long PerfTicks, long WallTicks) ReadLatencyProbe(ReadOnlySpan<byte> payload) =>
        (BinaryPrimitives.ReadInt64LittleEndian(payload[..8]), BinaryPrimitives.ReadInt64LittleEndian(payload[8..16]));

    /// <summary>Reads a <see cref="LanDatagramKind.LatencyEcho"/> datagram's payload.</summary>
    public static (long ProbePerfTicks, long ProbeWallTicks, long ClientWallTicks) ReadLatencyEcho(ReadOnlySpan<byte> payload) =>
        (BinaryPrimitives.ReadInt64LittleEndian(payload[..8]),
         BinaryPrimitives.ReadInt64LittleEndian(payload[8..16]),
         BinaryPrimitives.ReadInt64LittleEndian(payload[16..24]));

    /// <summary>Sent by the client, roughly once a second -- its own recent frame-loss rate (0..1), the feedback signal RemoteControl.Net.Congestion.CongestionController reacts to.</summary>
    public static byte[] CreateQualityReport(ulong sessionId, float frameLossRate)
    {
        var datagram = CreateHeader(LanDatagramKind.QualityReport, sessionId, QualityReportSize);
        BinaryPrimitives.WriteSingleLittleEndian(datagram.AsSpan(CommonHeaderSize, 4), frameLossRate);
        return datagram;
    }

    /// <summary>Reads a <see cref="LanDatagramKind.QualityReport"/> datagram's payload.</summary>
    public static float ReadQualityReport(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadSingleLittleEndian(payload[..4]);

    /// <summary>
    /// Sent by the client, one per captured mouse/keyboard event -- see
    /// RemoteControl.Input.RawInputCapture. Payload is a single
    /// RemoteControl.Protocol.InputEventCodec-encoded event, opaque to this
    /// envelope (same relationship as <see cref="WrapVideo"/> to a video
    /// shard). Best-effort UDP, same as video and everything else on this
    /// socket -- see docs/PHASE-3.md for the known risk that implies for a
    /// lost MouseUp/KeyUp specifically.
    /// </summary>
    public static byte[] WrapInput(ulong sessionId, ReadOnlySpan<byte> encodedInputEvent)
    {
        if (encodedInputEvent.IsEmpty)
            throw new ArgumentException("Encoded input event must not be empty.", nameof(encodedInputEvent));

        var datagram = CreateHeader(
            LanDatagramKind.Input,
            sessionId,
            checked(CommonHeaderSize + encodedInputEvent.Length));
        encodedInputEvent.CopyTo(datagram.AsSpan(CommonHeaderSize));
        return datagram;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out LanDatagram datagram)
    {
        datagram = default;
        if (source.Length < CommonHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
        {
            return false;
        }

        var kind = (LanDatagramKind)source[4];
        var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(source[5..13]);
        switch (kind)
        {
            case LanDatagramKind.Configuration when source.Length == ConfigurationSize:
                var width = BinaryPrimitives.ReadUInt32LittleEndian(source[13..17]);
                var height = BinaryPrimitives.ReadUInt32LittleEndian(source[17..21]);
                var fpsNumerator = BinaryPrimitives.ReadUInt32LittleEndian(source[21..25]);
                var fpsDenominator = BinaryPrimitives.ReadUInt32LittleEndian(source[25..29]);
                if (width == 0 || height == 0 || fpsNumerator == 0 || fpsDenominator == 0)
                    return false;

                datagram = new LanDatagram(
                    kind,
                    sessionId,
                    width,
                    height,
                    fpsNumerator,
                    fpsDenominator,
                    ReadOnlyMemory<byte>.Empty);
                return true;

            case LanDatagramKind.Ready when source.Length == CommonHeaderSize:
            case LanDatagramKind.End when source.Length == CommonHeaderSize:
                datagram = new LanDatagram(
                    kind,
                    sessionId,
                    0,
                    0,
                    0,
                    0,
                    ReadOnlyMemory<byte>.Empty);
                return true;

            case LanDatagramKind.Video when source.Length > CommonHeaderSize:
            case LanDatagramKind.LatencyProbe when source.Length == LatencyProbeSize:
            case LanDatagramKind.LatencyEcho when source.Length == LatencyEchoSize:
            case LanDatagramKind.QualityReport when source.Length == QualityReportSize:
            case LanDatagramKind.Input when source.Length > CommonHeaderSize:
                datagram = new LanDatagram(
                    kind,
                    sessionId,
                    0,
                    0,
                    0,
                    0,
                    source[CommonHeaderSize..].ToArray());
                return true;

            default:
                return false;
        }
    }

    private static byte[] CreateHeader(LanDatagramKind kind, ulong sessionId, int length)
    {
        var datagram = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, Magic);
        datagram[4] = (byte)kind;
        BinaryPrimitives.WriteUInt64LittleEndian(datagram.AsSpan(5, 8), sessionId);
        return datagram;
    }
}
