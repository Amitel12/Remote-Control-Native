using RemoteControl.Net.Transport;
using Xunit;

namespace RemoteControl.Net.Tests.Transport;

public class LanDatagramCodecTests
{
    [Fact]
    public void Configuration_RoundTrips()
    {
        var bytes = LanDatagramCodec.CreateConfiguration(0x123456789ABCDEF0, 1920, 1080, 60, 1);

        Assert.True(LanDatagramCodec.TryRead(bytes, out var datagram));
        Assert.Equal(LanDatagramKind.Configuration, datagram.Kind);
        Assert.Equal(0x123456789ABCDEF0UL, datagram.SessionId);
        Assert.Equal(1920u, datagram.Width);
        Assert.Equal(1080u, datagram.Height);
        Assert.Equal(60u, datagram.FpsNumerator);
        Assert.Equal(1u, datagram.FpsDenominator);
        Assert.True(datagram.Payload.IsEmpty);
    }

    [Fact]
    public void ReadyAndEnd_RoundTrip()
    {
        Assert.True(LanDatagramCodec.TryRead(LanDatagramCodec.CreateReady(7), out var ready));
        Assert.Equal(LanDatagramKind.Ready, ready.Kind);
        Assert.Equal(7UL, ready.SessionId);

        Assert.True(LanDatagramCodec.TryRead(LanDatagramCodec.CreateEnd(7), out var end));
        Assert.Equal(LanDatagramKind.End, end.Kind);
        Assert.Equal(7UL, end.SessionId);
    }

    [Fact]
    public void Video_RoundTripsWithoutAliasingInput()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var bytes = LanDatagramCodec.WrapVideo(9, payload);

        Assert.True(LanDatagramCodec.TryRead(bytes, out var datagram));
        bytes[^1] = 99;

        Assert.Equal(LanDatagramKind.Video, datagram.Kind);
        Assert.Equal(9UL, datagram.SessionId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, datagram.Payload.ToArray());
    }

    [Fact]
    public void LatencyProbeAndEcho_RoundTrip()
    {
        var probeBytes = LanDatagramCodec.CreateLatencyProbe(42, perfTicks: 123456789L, wallTicks: 987654321L);
        Assert.True(LanDatagramCodec.TryRead(probeBytes, out var probe));
        Assert.Equal(LanDatagramKind.LatencyProbe, probe.Kind);
        Assert.Equal(42UL, probe.SessionId);
        var (perfTicks, wallTicks) = LanDatagramCodec.ReadLatencyProbe(probe.Payload.Span);
        Assert.Equal(123456789L, perfTicks);
        Assert.Equal(987654321L, wallTicks);

        var echoBytes = LanDatagramCodec.CreateLatencyEcho(42, probePerfTicks: 123456789L, probeWallTicks: 987654321L, clientWallTicks: 111222333L);
        Assert.True(LanDatagramCodec.TryRead(echoBytes, out var echo));
        Assert.Equal(LanDatagramKind.LatencyEcho, echo.Kind);
        Assert.Equal(42UL, echo.SessionId);
        var (echoedPerfTicks, echoedWallTicks, clientWallTicks) = LanDatagramCodec.ReadLatencyEcho(echo.Payload.Span);
        Assert.Equal(123456789L, echoedPerfTicks);
        Assert.Equal(987654321L, echoedWallTicks);
        Assert.Equal(111222333L, clientWallTicks);
    }

    [Fact]
    public void QualityReport_RoundTrips()
    {
        var bytes = LanDatagramCodec.CreateQualityReport(42, frameLossRate: 0.125f);

        Assert.True(LanDatagramCodec.TryRead(bytes, out var datagram));
        Assert.Equal(LanDatagramKind.QualityReport, datagram.Kind);
        Assert.Equal(42UL, datagram.SessionId);
        Assert.Equal(0.125f, LanDatagramCodec.ReadQualityReport(datagram.Payload.Span));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31 })]
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31, 99, 0, 0, 0, 0, 0, 0, 0, 0 })]
    // LatencyProbe (kind 5) header with no payload -- too short for LatencyProbeSize.
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31, 5, 0, 0, 0, 0, 0, 0, 0, 0 })]
    // LatencyEcho (kind 6) header with only a probe-sized payload -- too short for LatencyEchoSize.
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31, 6, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 1, 2, 3, 4, 5, 6, 7, 8 })]
    // QualityReport (kind 7) header with no payload -- too short for QualityReportSize.
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31, 7, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void InvalidDatagrams_AreRejected(byte[] bytes)
    {
        Assert.False(LanDatagramCodec.TryRead(bytes, out _));
    }
}
