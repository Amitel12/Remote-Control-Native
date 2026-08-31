using RemoteControl.Net.Fec;
using RemoteControl.Protocol;

namespace RemoteControl.Net.Video;

/// <summary>
/// Reassembles frames from the packets VideoPacketizer produces, tolerating
/// out-of-order delivery, duplicate packets, and up to (TotalShards -
/// DataShards) lost packets per frame via Reed-Solomon reconstruction --
/// see docs/ARCHITECTURE.md's FEC design notes. Stateless about *time*:
/// it just assembles whatever frames it's fed and returns completed ones;
/// deciding when a stalled/incomplete frame is too old to keep waiting for
/// is the JitterBuffer/FramePacer's job (call <see cref="EvictFramesOlderThan"/>
/// once that decision is made), not this class's.
/// </summary>
public sealed class VideoDepacketizer
{
    private const int MaxInProgressFrames = 64;
    private const uint FrameRetentionWindow = 8;
    private readonly Dictionary<uint, FrameAssembly> _inProgress = new();
    private uint? _newestFrameIndex;

    // Highest frame index that will never be accepted again, whether it
    // completed or was evicted incomplete. Without this, a shard that only
    // arrived *late* (not actually lost -- e.g. reordered behind a burst of
    // later frames) for an already-evicted frame would silently reopen a
    // brand-new FrameAssembly for that index. Confirmed happening under real
    // loss + reordering (see docs/PHASE-1.md's FEC recovery test): it
    // double-counted the frame in both DroppedIncompleteFrameCount and a
    // later completion, and worse, a frame reopened this way can complete
    // and get decoded/presented *after* newer frames already displayed.
    private uint? _lastResolvedFrameIndex;

    public int DroppedIncompleteFrameCount { get; private set; }

    /// <summary>
    /// Feeds one received UDP payload (header + shard bytes, as produced by
    /// VideoPacketizer.Packetize). Returns the fully reassembled frame bytes
    /// once enough shards have arrived to reconstruct it, or null if more
    /// are still needed.
    /// </summary>
    public byte[]? AddPacket(ReadOnlySpan<byte> packet)
    {
        var header = VideoPacketHeader.ReadFrom(packet);
        var shardPayload = packet[VideoPacketHeader.Size..];
        ValidateHeader(header, shardPayload.Length);

        if (_newestFrameIndex is null || header.FrameIndex > _newestFrameIndex.Value)
        {
            _newestFrameIndex = header.FrameIndex;
            if (header.FrameIndex > FrameRetentionWindow)
                EvictFramesOlderThan(header.FrameIndex - FrameRetentionWindow);
        }

        // A shard for a frame that's already resolved (completed or evicted)
        // arrived late -- discard it rather than reopening a new assembly
        // from scratch. See _lastResolvedFrameIndex's remarks.
        if (_lastResolvedFrameIndex is not null &&
            header.FrameIndex <= _lastResolvedFrameIndex.Value &&
            !_inProgress.ContainsKey(header.FrameIndex))
        {
            return null;
        }

        if (!_inProgress.TryGetValue(header.FrameIndex, out var assembly))
        {
            if (_inProgress.Count >= MaxInProgressFrames)
                throw new InvalidOperationException($"Too many incomplete video frames (limit {MaxInProgressFrames}).");

            assembly = new FrameAssembly(
                header.FecDataShards,
                header.FecTotalShards,
                header.FrameByteLength,
                shardPayload.Length);
            _inProgress[header.FrameIndex] = assembly;
        }
        else if (!assembly.Matches(header, shardPayload.Length))
        {
            throw new ArgumentException("Video shards for one frame disagree about FEC or frame dimensions.", nameof(packet));
        }

        assembly.AddShard(header.FecShardIndex, shardPayload.ToArray());

        if (!assembly.CanReassemble) return null;

        var frameBytes = assembly.Reassemble();
        _inProgress.Remove(header.FrameIndex);
        if (_lastResolvedFrameIndex is null || header.FrameIndex > _lastResolvedFrameIndex.Value)
            _lastResolvedFrameIndex = header.FrameIndex;
        return frameBytes;
    }

    /// <summary>Drops any in-progress (incomplete) frames older than the given index -- call once the pacer has decided to stop waiting on them.</summary>
    public void EvictFramesOlderThan(uint frameIndex)
    {
        foreach (var key in _inProgress.Keys.Where(k => k < frameIndex).ToList())
        {
            _inProgress.Remove(key);
            DroppedIncompleteFrameCount++;
        }

        // Everything below this threshold is now permanently resolved (either
        // already completed, or given up on here), even indices that never
        // had an in-progress entry at all -- see _lastResolvedFrameIndex's remarks.
        if (frameIndex > 0 && (_lastResolvedFrameIndex is null || frameIndex - 1 > _lastResolvedFrameIndex.Value))
            _lastResolvedFrameIndex = frameIndex - 1;
    }

    public int InProgressFrameCount => _inProgress.Count;

    private static void ValidateHeader(VideoPacketHeader header, int shardPayloadLength)
    {
        if (shardPayloadLength <= 0)
            throw new ArgumentException("Video shard payload must not be empty.");
        if (header.FecDataShards == 0 ||
            header.FecTotalShards < header.FecDataShards ||
            header.FecTotalShards > VideoPacketizer.MaxTotalShards ||
            header.FecShardIndex >= header.FecTotalShards)
        {
            throw new ArgumentException("Video packet contains invalid FEC shard counts or index.");
        }

        var maximumFrameLength = (ulong)header.FecDataShards * (uint)shardPayloadLength;
        if (header.FrameByteLength == 0 || header.FrameByteLength > maximumFrameLength)
            throw new ArgumentException("Video packet frame length is inconsistent with its data shards.");
    }

    private sealed class FrameAssembly
    {
        private readonly byte[]?[] _shards;
        private readonly ushort _dataShards;
        private readonly ushort _totalShards;
        private readonly uint _frameByteLength;
        private readonly int _shardPayloadLength;
        private int _receivedCount;

        public FrameAssembly(ushort dataShards, ushort totalShards, uint frameByteLength, int shardPayloadLength)
        {
            _dataShards = dataShards;
            _totalShards = totalShards;
            _frameByteLength = frameByteLength;
            _shardPayloadLength = shardPayloadLength;
            _shards = new byte[totalShards][];
        }

        public bool Matches(VideoPacketHeader header, int shardPayloadLength) =>
            header.FecDataShards == _dataShards &&
            header.FecTotalShards == _totalShards &&
            header.FrameByteLength == _frameByteLength &&
            shardPayloadLength == _shardPayloadLength;

        public bool CanReassemble => _receivedCount >= _dataShards;

        public void AddShard(ushort shardIndex, byte[] shardBytes)
        {
            if (_shards[shardIndex] is not null) return; // duplicate, ignore
            _shards[shardIndex] = shardBytes;
            _receivedCount++;
        }

        public byte[] Reassemble()
        {
            byte[][] dataShardBytes;

            var haveAllDataShardsDirectly = true;
            for (var i = 0; i < _dataShards; i++)
            {
                if (_shards[i] is null) { haveAllDataShardsDirectly = false; break; }
            }

            if (haveAllDataShardsDirectly)
            {
                dataShardBytes = new byte[_dataShards][];
                for (var i = 0; i < _dataShards; i++) dataShardBytes[i] = _shards[i]!;
            }
            else
            {
                var receivedIndices = new List<int>();
                var receivedShards = new List<byte[]>();
                for (var i = 0; i < _totalShards && receivedIndices.Count < _dataShards; i++)
                {
                    if (_shards[i] is { } shard)
                    {
                        receivedIndices.Add(i);
                        receivedShards.Add(shard);
                    }
                }

                var codec = new ReedSolomonCodec(_dataShards, _totalShards);
                dataShardBytes = codec.Decode(receivedIndices, receivedShards);
            }

            var frame = new byte[_frameByteLength];
            var shardPayloadSize = dataShardBytes[0].Length;
            var offset = 0;
            foreach (var shard in dataShardBytes)
            {
                var remaining = frame.Length - offset;
                if (remaining <= 0) break;
                var toCopy = Math.Min(remaining, shardPayloadSize);
                shard.AsSpan(0, toCopy).CopyTo(frame.AsSpan(offset));
                offset += toCopy;
            }
            return frame;
        }
    }
}
