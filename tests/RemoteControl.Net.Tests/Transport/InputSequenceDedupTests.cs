using RemoteControl.Net.Transport;
using Xunit;

namespace RemoteControl.Net.Tests.Transport;

public class InputSequenceDedupTests
{
    [Fact]
    public void TryAccept_AcceptsAFreshSequenceNumberOnce()
    {
        var dedup = new InputSequenceDedup();

        Assert.True(dedup.TryAccept(1));
    }

    [Fact]
    public void TryAccept_RejectsAnImmediateDuplicate()
    {
        var dedup = new InputSequenceDedup();

        Assert.True(dedup.TryAccept(1));
        Assert.False(dedup.TryAccept(1));
    }

    [Fact]
    public void TryAccept_ReAcceptsASequenceNumberOnceItIsEvictedPastCapacity()
    {
        var dedup = new InputSequenceDedup(capacity: 2);

        Assert.True(dedup.TryAccept(1));
        Assert.True(dedup.TryAccept(2));
        Assert.False(dedup.TryAccept(2)); // still within the window -- duplicate check is active.

        // A 3rd acceptance pushes sequence 1 out of the bounded recently-seen window.
        Assert.True(dedup.TryAccept(3));

        // 1 was evicted, so it is treated as fresh rather than rejected as a duplicate.
        Assert.True(dedup.TryAccept(1));
    }
}
