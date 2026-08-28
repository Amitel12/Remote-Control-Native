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
    private readonly Dictionary<uint, FrameAssembly> _inProgress = new();

    /// <summary>
    /// Feeds one received UDP payload (header + shard bytes, as produced by
    /// VideoPacketizer.Packetize). Returns the fully reassembled frame bytes
    /// once enough shards have arrived to reconstruct it, or null if more
    /// are still needed.
    /// </summary>
    public byte[]? AddPacket(ReadOnlySpan<byte> packet)
    {
        var header = VideoPacketHeader.ReadFrom(packet);
        var shardBytes = packet[VideoPacketHeader.Size..].ToArray();

        if (!_inProgress.TryGetValue(header.FrameIndex, out var assembly))
        {
            assembly = new FrameAssembly(header.FecDataShards, header.FecTotalShards, header.FrameByteLength);
            _inProgress[header.FrameIndex] = assembly;
        }

        assembly.AddShard(header.FecShardIndex, shardBytes);

        if (!assembly.CanReassemble) return null;

        var frameBytes = assembly.Reassemble();
        _inProgress.Remove(header.FrameIndex);
        return frameBytes;
    }

    /// <summary>Drops any in-progress (incomplete) frames older than the given index -- call once the pacer has decided to stop waiting on them.</summary>
    public void EvictFramesOlderThan(uint frameIndex)
    {
        foreach (var key in _inProgress.Keys.Where(k => k < frameIndex).ToList())
        {
            _inProgress.Remove(key);
        }
    }

    public int InProgressFrameCount => _inProgress.Count;

    private sealed class FrameAssembly
    {
        private readonly byte[]?[] _shards;
        private readonly ushort _dataShards;
        private readonly ushort _totalShards;
        private readonly uint _frameByteLength;
        private int _receivedCount;

        public FrameAssembly(ushort dataShards, ushort totalShards, uint frameByteLength)
        {
            _dataShards = dataShards;
            _totalShards = totalShards;
            _frameByteLength = frameByteLength;
            _shards = new byte[totalShards][];
        }

        public bool CanReassemble => _receivedCount >= _dataShards;

        public void AddShard(ushort shardIndex, byte[] shardBytes)
        {
            if (shardIndex >= _totalShards) return; // malformed/stale packet, ignore rather than throw -- untrusted network input
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
