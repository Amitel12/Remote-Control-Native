using System.Buffers.Binary;

namespace RemoteControl.Net.Transport;

public enum LanDatagramKind : byte
{
    Configuration = 1,
    Ready = 2,
    Video = 3,
    End = 4,
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
