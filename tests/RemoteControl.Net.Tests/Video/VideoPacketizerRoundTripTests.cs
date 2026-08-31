using RemoteControl.Net.Video;
using RemoteControl.Protocol;
using Xunit;

namespace RemoteControl.Net.Tests.Video;

public class VideoPacketizerRoundTripTests
{
    [Fact]
    public void RoundTrips_WithNoLoss()
    {
        var frame = MakeFrame(5000, seed: 1);
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0.25);
        var depacketizer = new VideoDepacketizer();

        var packets = packetizer.Packetize(frameIndex: 1, frame);

        byte[]? result = null;
        foreach (var packet in packets)
        {
            result = depacketizer.AddPacket(packet);
            if (result is not null) break;
        }

        Assert.NotNull(result);
        Assert.True(frame.AsSpan().SequenceEqual(result));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.2)]
    public void RoundTrips_WithRandomPacketLoss_WithinFecBudget(double lossRatio)
    {
        // parityRatio=0.3 means roughly 30% of shards are parity, so losing
        // up to ~23% (0.3/1.3) of *all* shards should still be recoverable
        // -- pick a lossRatio comfortably under that so this test is
        // checking FEC actually works, not flaking near the boundary.
        var frame = MakeFrame(20_000, seed: 2);
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0.3);
        var depacketizer = new VideoDepacketizer();

        var packets = packetizer.Packetize(frameIndex: 42, frame);

        var rng = new Random(1234);
        var delivered = packets.Where(_ => rng.NextDouble() >= lossRatio).ToList();
        // Shuffle to also prove out-of-order delivery is handled.
        delivered = delivered.OrderBy(_ => rng.Next()).ToList();

        byte[]? result = null;
        foreach (var packet in delivered)
        {
            result = depacketizer.AddPacket(packet);
            if (result is not null) break;
        }

        Assert.NotNull(result);
        Assert.True(frame.AsSpan().SequenceEqual(result));
    }

    [Fact]
    public void DuplicatePackets_AreIgnored_NotCountedTwice()
    {
        var frame = MakeFrame(3000, seed: 3);
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0.25);
        var depacketizer = new VideoDepacketizer();
        var packets = packetizer.Packetize(frameIndex: 7, frame);

        byte[]? result = null;
        // Feed the very first packet three times before the rest -- if
        // duplicates were (incorrectly) counted, this would let the
        // assembly think it has more distinct shards than it actually does.
        depacketizer.AddPacket(packets[0]);
        depacketizer.AddPacket(packets[0]);
        depacketizer.AddPacket(packets[0]);
        foreach (var packet in packets.Skip(1))
        {
            result = depacketizer.AddPacket(packet);
            if (result is not null) break;
        }

        Assert.NotNull(result);
        Assert.True(frame.AsSpan().SequenceEqual(result));
    }

    [Fact]
    public void MultipleInterleavedFrames_ReassembleIndependently()
    {
        var frameA = MakeFrame(2000, seed: 10);
        var frameB = MakeFrame(4000, seed: 11);
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0.25);
        var depacketizer = new VideoDepacketizer();

        var packetsA = packetizer.Packetize(0, frameA);
        var packetsB = packetizer.Packetize(1, frameB);

        // Interleave: A0, B0, A1, B1, ...
        byte[]? resultA = null;
        byte[]? resultB = null;
        var maxLen = Math.Max(packetsA.Count, packetsB.Count);
        for (var i = 0; i < maxLen; i++)
        {
            if (i < packetsA.Count) resultA ??= depacketizer.AddPacket(packetsA[i]);
            if (i < packetsB.Count) resultB ??= depacketizer.AddPacket(packetsB[i]);
        }

        Assert.NotNull(resultA);
        Assert.NotNull(resultB);
        Assert.True(frameA.AsSpan().SequenceEqual(resultA));
        Assert.True(frameB.AsSpan().SequenceEqual(resultB));
    }

    [Fact]
    public void EvictFramesOlderThan_DropsIncompleteOldFrames()
    {
        var frame = MakeFrame(2000, seed: 20);
        var packetizer = new VideoPacketizer();
        var depacketizer = new VideoDepacketizer();
        var packets = packetizer.Packetize(5, frame);

        // Only feed one packet -- frame stays incomplete.
        depacketizer.AddPacket(packets[0]);
        Assert.Equal(1, depacketizer.InProgressFrameCount);

        depacketizer.EvictFramesOlderThan(frameIndex: 10);
        Assert.Equal(0, depacketizer.InProgressFrameCount);
    }

    [Fact]
    public void Packetize_ThrowsOnEmptyFrame()
    {
        var packetizer = new VideoPacketizer();
        Assert.Throws<ArgumentException>(() => packetizer.Packetize(0, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ZeroParity_ProducesOnlyDataShards_AndRoundTrips()
    {
        var frame = MakeFrame(2500, seed: 30);
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0);
        var depacketizer = new VideoDepacketizer();

        var packets = packetizer.Packetize(8, frame);

        Assert.Equal(3, packets.Count);
        byte[]? result = null;
        foreach (var packet in packets)
            result ??= depacketizer.AddPacket(packet);

        Assert.Equal(frame, result);
    }

    [Fact]
    public void MalformedFecCounts_AreRejectedBeforeAllocatingAssembly()
    {
        var packet = new byte[VideoPacketHeader.Size + 1];
        new VideoPacketHeader(
            FrameIndex: 1,
            FecShardIndex: 0,
            FecDataShards: 0,
            FecTotalShards: ushort.MaxValue,
            Flags: VideoPacketFlags.None,
            FrameByteLength: 1).WriteTo(packet);

        var depacketizer = new VideoDepacketizer();

        Assert.Throws<ArgumentException>(() => depacketizer.AddPacket(packet));
        Assert.Equal(0, depacketizer.InProgressFrameCount);
    }

    [Fact]
    public void StaleIncompleteFrame_IsEvictedWhenStreamMovesAhead()
    {
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0);
        var depacketizer = new VideoDepacketizer();
        var oldPackets = packetizer.Packetize(0, MakeFrame(2000, seed: 40));
        var newerPackets = packetizer.Packetize(9, MakeFrame(2000, seed: 41));

        depacketizer.AddPacket(oldPackets[0]);
        Assert.Equal(1, depacketizer.InProgressFrameCount);

        depacketizer.AddPacket(newerPackets[0]);

        Assert.Equal(1, depacketizer.DroppedIncompleteFrameCount);
        Assert.Equal(1, depacketizer.InProgressFrameCount);
    }

    [Fact]
    public void LateShardForAlreadyEvictedFrame_IsDiscarded_NotReopened()
    {
        // A shard that only arrived *late* (not lost -- e.g. reordered behind
        // a burst of later frames) for a frame already evicted as incomplete
        // must not resurrect it: that would double-count the frame (evicted
        // *and* later "completed") and, worse, could decode/present a stale
        // frame after newer ones already displayed. See VideoDepacketizer's
        // _lastResolvedFrameIndex remarks.
        var packetizer = new VideoPacketizer(shardPayloadSize: 1200, parityRatio: 0);
        var depacketizer = new VideoDepacketizer();
        var oldPackets = packetizer.Packetize(0, MakeFrame(2000, seed: 50));
        var newerPackets = packetizer.Packetize(9, MakeFrame(2000, seed: 51));

        depacketizer.AddPacket(oldPackets[0]); // Frame 0 stays incomplete (needs all 2 shards).
        depacketizer.AddPacket(newerPackets[0]); // Evicts frame 0 (9 - 8 retention window = 1).
        Assert.Equal(1, depacketizer.DroppedIncompleteFrameCount);

        // The rest of frame 0's shards trickle in late -- if they reopened a
        // new assembly, this would return the reassembled frame 0 bytes.
        byte[]? result = null;
        foreach (var packet in oldPackets.Skip(1))
            result ??= depacketizer.AddPacket(packet);

        Assert.Null(result);
        Assert.Equal(1, depacketizer.DroppedIncompleteFrameCount); // Not double-counted.
        Assert.Equal(1, depacketizer.InProgressFrameCount); // Only frame 9's assembly, not a reopened frame 0.
    }

    private static byte[] MakeFrame(int length, int seed)
    {
        var frame = new byte[length];
        new Random(seed).NextBytes(frame);
        return frame;
    }
}
