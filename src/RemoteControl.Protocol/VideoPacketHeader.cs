using System.Buffers.Binary;

namespace RemoteControl.Protocol;

/// <summary>
/// Fixed 15-byte header prefixing every video-channel UDP payload. Modeled
/// on Moonlight's NV_VIDEO_PACKET (moonlight-common-c's
/// VideoDepacketizer.c/rs.c) -- see docs/ARCHITECTURE.md's Net module
/// breakdown. Explicit little-endian Read/Write methods rather than a
/// marshaled/blittable struct layout: wire formats should never depend on
/// the runtime's struct layout rules, only on an explicit, documented byte
/// layout both peers agree on.
///
/// Layout (little-endian):
///   [0..4)   FrameIndex        uint32  monotonically increasing per encoded frame
///   [4..6)   FecShardIndex     uint16  this packet's shard index within its FEC block (0..FecTotalShards-1)
///   [6..8)   FecDataShards     uint16  K: number of real data shards in this FEC block
///   [8..10)  FecTotalShards    uint16  N: K + parity shards in this FEC block (N-K = recoverable losses)
///   [10]     Flags             byte    bit0=StartOfFrame, bit1=EndOfFrame, bit2=IsParityShard
///   [11..15) FrameByteLength   uint32  total original (pre-padding, pre-FEC) encoded frame length in bytes --
///                                      same value on every packet of the frame, data or parity; needed to
///                                      truncate zero-padding off the last data shard on reassembly, including
///                                      when reassembly comes entirely from FEC-recovered shards. uint32, not
///                                      uint16 -- a 1080p H.264 keyframe alone can exceed 64KB.
/// </summary>
public readonly record struct VideoPacketHeader(
    uint FrameIndex,
    ushort FecShardIndex,
    ushort FecDataShards,
    ushort FecTotalShards,
    VideoPacketFlags Flags,
    uint FrameByteLength)
{
    public const int Size = 15;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException($"Destination must be at least {Size} bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], FrameIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], FecShardIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], FecDataShards);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..10], FecTotalShards);
        destination[10] = (byte)Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[11..15], FrameByteLength);
    }

    public static VideoPacketHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) throw new ArgumentException($"Source must be at least {Size} bytes.", nameof(source));
        return new VideoPacketHeader(
            FrameIndex: BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
            FecShardIndex: BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]),
            FecDataShards: BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]),
            FecTotalShards: BinaryPrimitives.ReadUInt16LittleEndian(source[8..10]),
            Flags: (VideoPacketFlags)source[10],
            FrameByteLength: BinaryPrimitives.ReadUInt32LittleEndian(source[11..15]));
    }
}

[Flags]
public enum VideoPacketFlags : byte
{
    None = 0,
    StartOfFrame = 1 << 0,
    EndOfFrame = 1 << 1,
    IsParityShard = 1 << 2,
}
