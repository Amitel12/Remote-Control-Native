using RemoteControl.Protocol;
using Xunit;

namespace RemoteControl.Protocol.Tests;

public class VideoPacketHeaderTests
{
    [Fact]
    public void RoundTrips_ThroughWriteAndRead()
    {
        var header = new VideoPacketHeader(
            FrameIndex: 123456,
            FecShardIndex: 7,
            FecDataShards: 10,
            FecTotalShards: 13,
            Flags: VideoPacketFlags.StartOfFrame | VideoPacketFlags.IsParityShard,
            FrameByteLength: 150_000); // bigger than a uint16 could hold -- a realistic 1080p keyframe size

        Span<byte> buffer = stackalloc byte[VideoPacketHeader.Size];
        header.WriteTo(buffer);
        var decoded = VideoPacketHeader.ReadFrom(buffer);

        Assert.Equal(header, decoded);
    }

    [Fact]
    public void FrameByteLength_SupportsValuesLargerThan64KB()
    {
        var header = new VideoPacketHeader(1, 0, 1, 1, VideoPacketFlags.None, FrameByteLength: 500_000);
        Span<byte> buffer = stackalloc byte[VideoPacketHeader.Size];
        header.WriteTo(buffer);
        Assert.Equal(500_000u, VideoPacketHeader.ReadFrom(buffer).FrameByteLength);
    }

    [Fact]
    public void WriteTo_ThrowsOnUndersizedDestination()
    {
        var header = new VideoPacketHeader(1, 0, 1, 1, VideoPacketFlags.None, 10);
        var tooSmall = new byte[VideoPacketHeader.Size - 1];
        Assert.Throws<ArgumentException>(() => header.WriteTo(tooSmall));
    }
}
