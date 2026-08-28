using RemoteControl.Net.Fec;
using Xunit;

namespace RemoteControl.Net.Tests.Fec;

public class GF256Tests
{
    [Fact]
    public void Multiply_ByZero_IsAlwaysZero()
    {
        for (var a = 0; a < 256; a++)
        {
            Assert.Equal(0, GF256.Multiply((byte)a, 0));
            Assert.Equal(0, GF256.Multiply(0, (byte)a));
        }
    }

    [Fact]
    public void Multiply_ByOne_IsIdentity()
    {
        for (var a = 0; a < 256; a++)
        {
            Assert.Equal((byte)a, GF256.Multiply((byte)a, 1));
        }
    }

    [Fact]
    public void Multiply_IsCommutative_ForSample()
    {
        for (byte a = 1; a != 0; a++)
        {
            for (byte b = 1; b != 0; b++)
            {
                Assert.Equal(GF256.Multiply(a, b), GF256.Multiply(b, a));
            }
        }
    }

    [Fact]
    public void Inverse_TimesOriginal_IsOne_ForAllNonZeroElements()
    {
        for (var a = 1; a < 256; a++)
        {
            var inv = GF256.Inverse((byte)a);
            Assert.Equal(1, GF256.Multiply((byte)a, inv));
        }
    }

    [Fact]
    public void Inverse_OfZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => GF256.Inverse(0));
    }

    [Fact]
    public void Divide_ThenMultiply_RecoversOriginal()
    {
        for (var a = 0; a < 256; a++)
        {
            for (byte b = 1; b != 0; b++)
            {
                var quotient = GF256.Divide((byte)a, b);
                Assert.Equal((byte)a, GF256.Multiply(quotient, b));
            }
        }
    }

    [Fact]
    public void Pow_MatchesRepeatedMultiplication()
    {
        for (byte a = 1; a != 0; a++)
        {
            byte expected = 1;
            for (var power = 0; power < 10; power++)
            {
                Assert.Equal(expected, GF256.Pow(a, power));
                expected = GF256.Multiply(expected, a);
            }
        }
    }

    [Fact]
    public void AllNonZeroElements_AreDistinctPowersOfGenerator()
    {
        // A correctly constructed log/antilog table for a *primitive*
        // polynomial means GF256.Pow(2, i) for i in 0..254 enumerates all
        // 255 non-zero field elements exactly once -- this is the strongest
        // single check that the table generation (and the choice of 0x11D
        // as a primitive polynomial) is actually correct, not just
        // self-consistent.
        var seen = new HashSet<byte>();
        for (var i = 0; i < 255; i++)
        {
            var value = GF256.Pow(2, i);
            Assert.True(seen.Add(value), $"Value {value} repeated at power {i} -- polynomial is not primitive or table is wrong.");
        }
        Assert.Equal(255, seen.Count);
    }
}
