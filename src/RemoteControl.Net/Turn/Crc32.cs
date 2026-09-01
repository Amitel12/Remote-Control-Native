namespace RemoteControl.Net.Turn;

/// <summary>
/// CRC-32 (the ISO-HDLC/zlib variant, polynomial 0xEDB88320 reflected), needed only for STUN's
/// FINGERPRINT attribute (RFC 5389 section 15.5). .NET has no CRC-32 in the box outside
/// System.IO.Hashing, which would be a new package reference for 30 lines of table lookup.
/// Not tested directly (it is internal): it is pinned end to end by TurnMessageTests' byte-exact
/// reference vector, whose FINGERPRINT was produced by Python's zlib.crc32 -- a genuinely
/// separate implementation, which is the point.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (var i = 0u; i < 256u; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
                entry = (entry & 1) != 0 ? (entry >> 1) ^ polynomial : entry >> 1;
            table[i] = entry;
        }
        return table;
    }
}
