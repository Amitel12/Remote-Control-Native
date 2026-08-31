namespace RemoteControl.Tools.LossyProxy;

/// <summary>
/// Decides, per relayed packet, whether to drop it and/or delay it extra --
/// the actual "lossy/reordering" behavior docs/ARCHITECTURE.md's Phase 4
/// asks for, as an alternative to LoopbackHarness's synthetic independent
/// per-shard --drop-percent.
/// </summary>
internal sealed class PacketImpairment
{
    private readonly Random _rng = new();
    private readonly int _lossPercent;
    private readonly bool _burstLoss;
    private readonly int _reorderPercent;
    private readonly (int Min, int Max) _reorderDelayMs;
    private readonly (int Min, int Max) _jitterMs;

    // Gilbert-Elliott two-state model: "bad" state drops at _lossPercent, "good" state never drops.
    // Transition probabilities chosen for a mean burst length of a few packets once in the bad state --
    // real network loss is correlated (one lost packet strongly predicts the next few are too), unlike
    // independent per-packet Bernoulli loss.
    private const double GoodToBad = 0.05;
    private const double BadToGood = 0.30;
    private bool _inBadState;

    public PacketImpairment(int lossPercent, bool burstLoss, int reorderPercent, (int, int) reorderDelayMs, (int, int) jitterMs)
    {
        _lossPercent = lossPercent;
        _burstLoss = burstLoss;
        _reorderPercent = reorderPercent;
        _reorderDelayMs = reorderDelayMs;
        _jitterMs = jitterMs;
    }

    public (bool Drop, int DelayMs) Decide()
    {
        var drop = _lossPercent > 0 && (_burstLoss ? DecideBurstDrop() : Roll(_lossPercent));

        var delayMs = 0;
        if (!drop)
        {
            if (_jitterMs.Max > 0)
                delayMs += _rng.Next(_jitterMs.Min, _jitterMs.Max + 1);
            if (_reorderPercent > 0 && Roll(_reorderPercent))
                delayMs += _rng.Next(_reorderDelayMs.Min, _reorderDelayMs.Max + 1);
        }

        return (drop, delayMs);
    }

    private bool DecideBurstDrop()
    {
        _inBadState = _inBadState ? _rng.NextDouble() >= BadToGood : _rng.NextDouble() < GoodToBad;
        return _inBadState && Roll(_lossPercent);
    }

    private bool Roll(int percent) => _rng.Next(100) < percent;
}
