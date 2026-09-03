namespace RemoteControl.Net.Transport;

/// <summary>
/// Dedups redundant copies of the same input event (docs/PHASE-3.md) by
/// exact sequence-number membership in a bounded recently-seen window,
/// rather than by strict "reject anything not greater than the last
/// applied" ordering. The strict-ordering version has a real bug: within
/// one KeyDown/KeyUp pair, the KeyUp (numerically higher sequence number)
/// can get applied before the KeyDown's redundant retry arrives, and a
/// strict gate then rejects that still-useful retry as "stale" purely
/// because a *different*, unrelated, numerically-larger sequence number
/// happened to be applied first -- confirmed empirically via
/// tools/LoopbackHarness's --input-reliability-demo (measured recovery
/// matched the predicted cost of this exact bug almost exactly). This
/// class only rejects a sequence number it has genuinely already accepted.
/// </summary>
public sealed class InputSequenceDedup
{
    private readonly int _capacity;
    private readonly HashSet<uint> _seen;
    private readonly Queue<uint> _order = new();

    public InputSequenceDedup(int capacity = 64)
    {
        _capacity = capacity;
        _seen = new HashSet<uint>(capacity);
    }

    /// <summary>True and records the sequence number if this is the first time it's been seen; false if it's a duplicate.</summary>
    public bool TryAccept(uint sequenceNumber)
    {
        if (!_seen.Add(sequenceNumber))
            return false;

        _order.Enqueue(sequenceNumber);
        if (_order.Count > _capacity)
            _seen.Remove(_order.Dequeue());
        return true;
    }
}
