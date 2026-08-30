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

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31 })]
    [InlineData(new byte[] { 0x52, 0x43, 0x4E, 0x31, 99, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void InvalidDatagrams_AreRejected(byte[] bytes)
    {
        Assert.False(LanDatagramCodec.TryRead(bytes, out _));
    }
}
