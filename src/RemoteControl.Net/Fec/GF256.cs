namespace RemoteControl.Net.Fec;

/// <summary>
/// Arithmetic in GF(2^8) using log/antilog tables, generated from the
/// primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D) -- the same
/// polynomial used by most standard Reed-Solomon implementations (e.g. QR
/// codes). Addition/subtraction in this field is just XOR (done inline by
/// callers); this class only needs to provide multiply/divide/inverse, the
/// non-trivial operations.
/// </summary>
public static class GF256
{
    private const int FieldSize = 256;
    private const int Poly = 0x11D;

    private static readonly byte[] LogTable = new byte[FieldSize];
    // Sized to 2*(FieldSize-1) so Multiply can index [log(a)+log(b)] directly
    // (max 254+254=508) without a modulo on every call.
    private static readonly byte[] ExpTable = new byte[2 * (FieldSize - 1)];

    static GF256()
    {
        var x = 1;
        for (var i = 0; i < FieldSize - 1; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if (x >= FieldSize) x ^= Poly;
        }
        for (var i = FieldSize - 1; i < ExpTable.Length; i++)
        {
            ExpTable[i] = ExpTable[i - (FieldSize - 1)];
        }
    }

    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return ExpTable[LogTable[a] + LogTable[b]];
    }

    public static byte Inverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException("0 has no multiplicative inverse in GF(256).");
        return ExpTable[(FieldSize - 1) - LogTable[a]];
    }

    public static byte Divide(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("Division by zero in GF(256).");
        if (a == 0) return 0;
        var diff = LogTable[a] - LogTable[b];
        if (diff < 0) diff += FieldSize - 1;
        return ExpTable[diff];
    }

    /// <summary>a^power, used only to build the Vandermonde matrix (small, non-hot-path).</summary>
    public static byte Pow(byte a, int power)
    {
        if (power == 0) return 1;
        if (a == 0) return 0;
        var log = (LogTable[a] * (long)power) % (FieldSize - 1);
        return ExpTable[(int)log];
    }
}
