using RemoteControl.Net.Congestion;
using Xunit;

namespace RemoteControl.Net.Tests.Congestion;

public class CongestionControllerTests
{
    [Fact]
    public void LossAboveThreshold_DecreasesBitrateMultiplicatively()
    {
        var controller = new CongestionController(startingBitrateBps: 8_000_000, minBitrateBps: 1_000_000, maxBitrateBps: 8_000_000, decreaseFactor: 0.8);

        var result = controller.OnSample(frameLossRate: 0.10, rttMs: null);

        Assert.Equal(6_400_000u, result);
        Assert.Equal(6_400_000u, controller.CurrentBitrateBps);
    }

    [Fact]
    public void LossAtOrBelowThreshold_DoesNotDecrease()
    {
        var controller = new CongestionController(8_000_000, 1_000_000, 8_000_000, lossThreshold: 0.02);

        var result = controller.OnSample(frameLossRate: 0.02, rttMs: null);

        Assert.Equal(8_000_000u, result);
    }

    [Fact]
    public void FirstRttSample_SeedsBaseline_NeverCountsAsASpike()
    {
        var controller = new CongestionController(8_000_000, 1_000_000, 8_000_000);

        var result = controller.OnSample(frameLossRate: null, rttMs: 500); // huge RTT, but it's the first sample -- becomes the baseline, not a spike.

        Assert.Equal(8_000_000u, result);
    }

    [Fact]
    public void RttWellAboveBaseline_DecreasesBitrate()
    {
        var controller = new CongestionController(8_000_000, 1_000_000, 8_000_000, rttSpikeMultiplier: 1.5, decreaseFactor: 0.8);
        controller.OnSample(null, rttMs: 20); // establish a 20ms baseline.

        var result = controller.OnSample(null, rttMs: 40); // 2x baseline > 1.5x threshold.

        Assert.Equal(6_400_000u, result);
    }

    [Fact]
    public void SustainedCleanSamples_IncreasesBitrateAdditively_AfterEnoughSamples()
    {
        var controller = new CongestionController(4_000_000, 1_000_000, 8_000_000, increaseFactor: 1.1, cleanSamplesBeforeIncrease: 3);

        uint last = 4_000_000;
        for (var i = 0; i < 2; i++)
            last = controller.OnSample(frameLossRate: 0, rttMs: 10);
        Assert.Equal(4_000_000u, last); // not enough clean samples yet.

        last = controller.OnSample(frameLossRate: 0, rttMs: 10); // third clean sample.

        Assert.Equal(4_400_000u, last);
    }

    [Fact]
    public void Bitrate_NeverExceedsConfiguredMax()
    {
        var controller = new CongestionController(7_900_000, 1_000_000, 8_000_000, increaseFactor: 2.0, cleanSamplesBeforeIncrease: 1);

        var result = controller.OnSample(frameLossRate: 0, rttMs: 10);

        Assert.Equal(8_000_000u, result);
    }

    [Fact]
    public void Bitrate_NeverDropsBelowConfiguredMin()
    {
        var controller = new CongestionController(1_100_000, 1_000_000, 8_000_000, decreaseFactor: 0.1);

        var result = controller.OnSample(frameLossRate: 0.5, rttMs: null);

        Assert.Equal(1_000_000u, result);
    }

    [Fact]
    public void SamplesWithNoSignalAtAll_DoNotCountTowardIncreasing()
    {
        var controller = new CongestionController(4_000_000, 1_000_000, 8_000_000, cleanSamplesBeforeIncrease: 2);

        controller.OnSample(null, null);
        controller.OnSample(null, null);
        var result = controller.OnSample(null, null);

        Assert.Equal(4_000_000u, result);
    }

    [Fact]
    public void LossEvent_ResetsCleanSampleStreak()
    {
        var controller = new CongestionController(4_000_000, 1_000_000, 8_000_000, cleanSamplesBeforeIncrease: 2, increaseFactor: 1.1, decreaseFactor: 0.9);

        controller.OnSample(0, 10); // one clean sample.
        controller.OnSample(0.5, 10); // loss event -- resets the streak and decreases.
        var result = controller.OnSample(0, 10); // only one clean sample since the reset -- not enough to increase yet.

        Assert.True(result < 4_000_000); // still down from the loss event, not back up.
    }

    [Fact]
    public void ConstructorRejects_StartingBitrateOutsideMinMaxRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CongestionController(10_000_000, 1_000_000, 8_000_000));
    }
}
