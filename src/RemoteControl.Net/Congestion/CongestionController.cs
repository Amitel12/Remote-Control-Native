namespace RemoteControl.Net.Congestion;

/// <summary>
/// Decides a target encoder bitrate from periodic network-health samples --
/// see docs/ARCHITECTURE.md Phase 4 and docs/PHASE-4.md. Classic AIMD
/// (additive-increase/multiplicative-decrease, the same shape TCP congestion
/// control uses): back off hard and fast the moment things look bad, climb
/// back up slowly and only once conditions have been clean for a while.
/// Reacts to two independent signals, either one enough to back off:
/// <list type="bullet">
/// <item>Client-reported frame loss rate above <see cref="LossThreshold"/> --
/// frames FEC couldn't recover, a direct sign the current bitrate/loss
/// combination is already too much for the link.</item>
/// <item>RTT rising well above its own recent baseline -- classic queueing/
/// bufferbloat congestion, which shows up before loss usually does.</item>
/// </list>
/// Deliberately stateless about *how* the caller measured these (the LAN
/// harness feeds it from <see cref="Transport.LanDatagramCodec"/>'s
/// QualityReport/latency-probe payloads; a future ENet-based transport could
/// feed it from whatever its own loss/RTT signals are).
/// </summary>
public sealed class CongestionController
{
    private readonly uint _minBitrateBps;
    private readonly uint _maxBitrateBps;
    private double _rttBaselineMs = -1;
    private int _consecutiveCleanSamples;
    // Soft ceiling for increases, separate from the hard _maxBitrateBps -- see remarks on
    // OnSample's decrease branch for why (docs/PHASE-4.md, real cellular-link tuning).
    private uint _recentCeilingBps;

    public double LossThreshold { get; }
    public double RttSpikeMultiplier { get; }
    public double DecreaseFactor { get; }
    public double IncreaseFactor { get; }
    public int CleanSamplesBeforeIncrease { get; }

    public uint CurrentBitrateBps { get; private set; }

    public CongestionController(
        uint startingBitrateBps,
        uint minBitrateBps,
        uint maxBitrateBps,
        double lossThreshold = 0.02,
        double rttSpikeMultiplier = 1.5,
        double decreaseFactor = 0.85,
        double increaseFactor = 1.05,
        int cleanSamplesBeforeIncrease = 5)
    {
        if (minBitrateBps == 0 || minBitrateBps > maxBitrateBps)
            throw new ArgumentOutOfRangeException(nameof(minBitrateBps), "Requires 0 < min <= max.");
        if (startingBitrateBps < minBitrateBps || startingBitrateBps > maxBitrateBps)
            throw new ArgumentOutOfRangeException(nameof(startingBitrateBps), "Must be within [min, max].");

        _minBitrateBps = minBitrateBps;
        _maxBitrateBps = maxBitrateBps;
        _recentCeilingBps = maxBitrateBps;
        CurrentBitrateBps = startingBitrateBps;
        LossThreshold = lossThreshold;
        RttSpikeMultiplier = rttSpikeMultiplier;
        DecreaseFactor = decreaseFactor;
        IncreaseFactor = increaseFactor;
        CleanSamplesBeforeIncrease = cleanSamplesBeforeIncrease;
    }

    /// <summary>
    /// Feed one sample (a QualityReport's loss rate and/or a fresh RTT
    /// measurement -- either can be omitted if that signal wasn't available
    /// this round). Returns the new target bitrate; unchanged from
    /// <see cref="CurrentBitrateBps"/> if nothing warranted a change yet.
    /// </summary>
    public uint OnSample(double? frameLossRate, double? rttMs)
    {
        var rttSpiked = false;
        if (rttMs is { } rtt)
        {
            if (_rttBaselineMs < 0)
                _rttBaselineMs = rtt; // first sample seeds the baseline rather than immediately looking like a spike.
            else
                rttSpiked = rtt > _rttBaselineMs * RttSpikeMultiplier;
        }

        var lossExceeded = frameLossRate is { } loss && loss > LossThreshold;

        if (lossExceeded || rttSpiked)
        {
            _consecutiveCleanSamples = 0;
            // Remember the level that just proved too much as a *soft* ceiling for future
            // increases, instead of always climbing straight back toward the hard configured max
            // -- without this, a link sitting right at its real capacity produces a repeating
            // climb-to-max-then-immediately-crash cycle (observed for real on a marginal cellular
            // link, docs/PHASE-4.md), wasting the climbed-back quality every time. Relaxed back
            // upward below once a sustained clean run proves out right at this ceiling.
            _recentCeilingBps = CurrentBitrateBps;
            var decreased = (uint)Math.Max(_minBitrateBps, CurrentBitrateBps * DecreaseFactor);
            CurrentBitrateBps = decreased;
            // A real spike/loss event re-baselines RTT too -- otherwise a sustained-but-stable
            // higher RTT after backing off bitrate would keep reading as "still spiking" forever.
            if (rttMs is { } sample) _rttBaselineMs = sample;
            return CurrentBitrateBps;
        }

        // Only track "clean" against samples that actually carried a signal -- a report with
        // neither loss nor RTT data shouldn't count toward climbing back up.
        if (frameLossRate is null && rttMs is null)
            return CurrentBitrateBps;

        _consecutiveCleanSamples++;
        if (_consecutiveCleanSamples >= CleanSamplesBeforeIncrease)
        {
            _consecutiveCleanSamples = 0;

            if (CurrentBitrateBps >= _recentCeilingBps && _recentCeilingBps < _maxBitrateBps)
            {
                // Pinned at the soft ceiling for a whole extra clean streak -- the link may have
                // genuinely recovered past whatever caused the last backoff, so earn back a
                // little more headroom to cautiously re-probe higher.
                _recentCeilingBps = (uint)Math.Min(_maxBitrateBps, _recentCeilingBps * IncreaseFactor);
            }

            if (CurrentBitrateBps < _recentCeilingBps)
                CurrentBitrateBps = (uint)Math.Min(_recentCeilingBps, CurrentBitrateBps * IncreaseFactor);

            if (rttMs is { } sample) _rttBaselineMs = sample; // slowly track a genuinely-improved baseline too.
        }

        return CurrentBitrateBps;
    }
}
