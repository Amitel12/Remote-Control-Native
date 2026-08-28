namespace RemoteControl.Net.Jitter;

/// <summary>
/// Decides when a reassembled frame is ready to hand to the decoder. Per
/// docs/ARCHITECTURE.md: both Moonlight and Sunshine found deep smoothing
/// buffers make stutter *worse*, not better, for this class of app -- so
/// this holds roughly one frame interval of slack (adaptive, not fixed)
/// and otherwise passes frames straight through, preferring to skip a late
/// frame over stalling the pipeline waiting for it.
///
/// This class only tracks *timing* -- it doesn't own frame reassembly
/// (VideoDepacketizer) or decoding. The intended wiring: as soon as
/// VideoDepacketizer.AddPacket returns a completed frame, call
/// <see cref="OnFrameReady"/> immediately; separately, call
/// <see cref="ShouldSkipStaleFrame"/> before decoding a frame that arrived
/// unusually late (e.g. because FEC recovery took a retransmission-free but
/// still non-zero amount of compute) to decide whether it's worth decoding
/// at all versus waiting for the next one.
/// </summary>
public sealed class FramePacer
{
    private readonly Func<DateTime> _clock;
    private double _emaFrameIntervalMs;
    private DateTime? _lastFrameReadyAt;
    private bool _hasSample;

    /// <summary>Extra slack added on top of the measured average frame interval before a frame counts as "late" -- keeps single-frame jitter from constantly triggering skips.</summary>
    public double SlackMs { get; }

    /// <summary>Smoothing factor for the exponential moving average of frame-to-frame interval (0..1, higher = adapts faster).</summary>
    public double EmaAlpha { get; }

    public FramePacer(double slackMs = 8.0, double emaAlpha = 0.2, Func<DateTime>? clock = null)
    {
        SlackMs = slackMs;
        EmaAlpha = emaAlpha;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Current adaptive budget: how late a frame can be (relative to the expected next-frame time) before it's considered worth skipping. Starts generous until enough samples exist to estimate a real cadence.</summary>
    public double CurrentBudgetMs => _hasSample ? _emaFrameIntervalMs + SlackMs : double.MaxValue;

    /// <summary>Call the instant a frame finishes reassembling (VideoDepacketizer.AddPacket returns non-null). Updates the adaptive interval estimate.</summary>
    public void OnFrameReady()
    {
        var now = _clock();
        if (_lastFrameReadyAt is { } previous)
        {
            var intervalMs = (now - previous).TotalMilliseconds;
            _emaFrameIntervalMs = _hasSample
                ? (EmaAlpha * intervalMs) + ((1 - EmaAlpha) * _emaFrameIntervalMs)
                : intervalMs;
            _hasSample = true;
        }
        _lastFrameReadyAt = now;
    }

    /// <summary>
    /// True if a frame that just became ready arrived so much later than
    /// the last one that decoding it is more likely to add visible lag
    /// than to help -- the caller should drop it and wait for the next
    /// frame instead of pushing it through the decode/render pipeline.
    /// </summary>
    public bool ShouldSkipStaleFrame(DateTime frameReadyAt)
    {
        if (!_hasSample || _lastFrameReadyAt is not { } lastReadyBeforeThisOne) return false;
        var sinceLastMs = (frameReadyAt - lastReadyBeforeThisOne).TotalMilliseconds;
        return sinceLastMs > CurrentBudgetMs;
    }

    public void Reset()
    {
        _lastFrameReadyAt = null;
        _hasSample = false;
        _emaFrameIntervalMs = 0;
    }
}
