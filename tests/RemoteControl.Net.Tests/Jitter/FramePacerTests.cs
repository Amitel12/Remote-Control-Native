using RemoteControl.Net.Jitter;
using Xunit;

namespace RemoteControl.Net.Tests.Jitter;

public class FramePacerTests
{
    [Fact]
    public void ShouldSkipStaleFrame_IsFalse_BeforeAnyIntervalIsMeasured()
    {
        var pacer = new FramePacer();
        Assert.False(pacer.ShouldSkipStaleFrame(DateTime.UtcNow));
    }

    [Fact]
    public void ShouldSkipStaleFrame_IsFalse_ForNormalCadence()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pacer = new FramePacer(slackMs: 8, clock: () => now);

        // Establish a steady ~16.6ms (60fps) cadence.
        for (var i = 0; i < 10; i++)
        {
            pacer.OnFrameReady();
            now = now.AddMilliseconds(16.6);
        }

        Assert.False(pacer.ShouldSkipStaleFrame(now));
    }

    [Fact]
    public void ShouldSkipStaleFrame_IsTrue_WhenFrameArrivesFarLaterThanEstablishedCadence()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pacer = new FramePacer(slackMs: 8, clock: () => now);

        for (var i = 0; i < 10; i++)
        {
            pacer.OnFrameReady();
            now = now.AddMilliseconds(16.6);
        }

        // A frame arriving 200ms after the last one, against an established
        // ~16.6ms cadence, should read as stale -- decoding it is more
        // likely to add lag than help; better to wait for the next frame.
        var lateArrival = now.AddMilliseconds(200);
        Assert.True(pacer.ShouldSkipStaleFrame(lateArrival));
    }

    [Fact]
    public void CurrentBudgetMs_AdaptsToMeasuredCadence_NotAFixedConstant()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fastPacer = new FramePacer(slackMs: 5, clock: () => now);
        for (var i = 0; i < 10; i++) { fastPacer.OnFrameReady(); now = now.AddMilliseconds(16.6); } // 60fps

        var slowNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var slowPacer = new FramePacer(slackMs: 5, clock: () => slowNow);
        for (var i = 0; i < 10; i++) { slowPacer.OnFrameReady(); slowNow = slowNow.AddMilliseconds(33.3); } // 30fps

        // A pacer that's learned a 30fps cadence should tolerate a longer
        // gap before calling a frame "stale" than one that's learned 60fps
        // -- the budget must adapt, not be a single hardcoded constant.
        Assert.True(slowPacer.CurrentBudgetMs > fastPacer.CurrentBudgetMs);
    }

    [Fact]
    public void Reset_ClearsLearnedCadence()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pacer = new FramePacer(clock: () => now);
        for (var i = 0; i < 5; i++) { pacer.OnFrameReady(); now = now.AddMilliseconds(16.6); }

        pacer.Reset();

        Assert.Equal(double.MaxValue, pacer.CurrentBudgetMs);
    }
}
