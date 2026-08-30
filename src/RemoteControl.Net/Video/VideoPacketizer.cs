using RemoteControl.Net.Fec;
using RemoteControl.Protocol;

namespace RemoteControl.Net.Video;

/// <summary>
/// Splits one encoded video frame into UDP-sized packets (header + data
/// shard, or header + parity shard) ready to hand to the unreliable video
/// channel. See VideoDepacketizer for the receive side.
///
/// v1 simplification (documented, not hidden): a whole frame's data shards
/// must fit in one FEC block (RemoteControl.Net.Fec.ReedSolomonCodec caps
/// total shards at 256, i.e. GF(256)'s size). At ShardPayloadSize=1200 and
/// a generous parity ratio, that's roughly a 200KB frame budget -- Moonlight
/// splits oversized frames across multiple independent FEC blocks; this
/// implementation throws instead of silently truncating if a frame doesn't
/// fit. Add multi-block splitting (would need a block-index field in
/// VideoPacketHeader, which doesn't exist yet) if real encoder output ever
/// exceeds that budget -- unlikely for 1080p H.264 delta frames, plausible
/// for very large keyframes at high bitrate, worth watching in Phase 1
/// testing.
/// </summary>
public sealed class VideoPacketizer
{
    public const int DefaultShardPayloadSize = 1200;
    public const int MaxTotalShards = 256;

    private readonly int _shardPayloadSize;
    private readonly double _parityRatio;

    public VideoPacketizer(int shardPayloadSize = DefaultShardPayloadSize, double parityRatio = 0.25)
    {
        if (shardPayloadSize <= 0) throw new ArgumentOutOfRangeException(nameof(shardPayloadSize));
        if (parityRatio < 0) throw new ArgumentOutOfRangeException(nameof(parityRatio));
        _shardPayloadSize = shardPayloadSize;
        _parityRatio = parityRatio;
    }

    public IReadOnlyList<byte[]> Packetize(uint frameIndex, ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty) throw new ArgumentException("Frame must not be empty.", nameof(frame));

        var dataShardCount = (frame.Length + _shardPayloadSize - 1) / _shardPayloadSize;
        var parityShardCount = _parityRatio == 0
            ? 0
            : Math.Max(1, (int)Math.Ceiling(dataShardCount * _parityRatio));
        var totalShardCount = dataShardCount + parityShardCount;

        if (totalShardCount > MaxTotalShards)
        {
            throw new InvalidOperationException(
                $"Frame {frameIndex} needs {dataShardCount} data + {parityShardCount} parity = {totalShardCount} shards, " +
                $"exceeding the single-FEC-block cap of {MaxTotalShards}. See VideoPacketizer's class doc for the v1 limitation.");
        }

        var dataShards = new byte[dataShardCount][];
        for (var i = 0; i < dataShardCount; i++)
        {
            var shard = new byte[_shardPayloadSize];
            var sourceStart = i * _shardPayloadSize;
            var sourceLength = Math.Min(_shardPayloadSize, frame.Length - sourceStart);
            frame.Slice(sourceStart, sourceLength).CopyTo(shard);
            dataShards[i] = shard;
        }

        var codec = new ReedSolomonCodec(dataShardCount, totalShardCount);
        var allShards = codec.EncodeParity(dataShards);

        var packets = new byte[totalShardCount][];
        for (var shardIndex = 0; shardIndex < totalShardCount; shardIndex++)
        {
            var flags = VideoPacketFlags.None;
            if (shardIndex == 0) flags |= VideoPacketFlags.StartOfFrame;
            if (shardIndex == dataShardCount - 1) flags |= VideoPacketFlags.EndOfFrame;
            if (shardIndex >= dataShardCount) flags |= VideoPacketFlags.IsParityShard;

            var header = new VideoPacketHeader(
                frameIndex,
                (ushort)shardIndex,
                (ushort)dataShardCount,
                (ushort)totalShardCount,
                flags,
                (uint)frame.Length);

            var packet = new byte[VideoPacketHeader.Size + _shardPayloadSize];
            header.WriteTo(packet);
            allShards[shardIndex].CopyTo(packet.AsSpan(VideoPacketHeader.Size));
            packets[shardIndex] = packet;
        }

        return packets;
    }
}
