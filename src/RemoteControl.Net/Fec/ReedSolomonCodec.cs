namespace RemoteControl.Net.Fec;

/// <summary>
/// Systematic Reed-Solomon erasure coding over GF(256): given K data
/// shards, produces N total shards (K data + (N-K) parity) such that ANY K
/// of the N shards reconstruct all K original data shards, with no
/// retransmission -- this is what lets the video channel survive UDP packet
/// loss without adding a round trip. Follows Moonlight's approach
/// (moonlight-common-c's reedsolomon/rs.c) and the standard
/// Vandermonde-based construction (Plank, "A tutorial on Reed-Solomon
/// coding for fault-tolerance in RAID-like systems").
///
/// Construction: build an N x K Vandermonde matrix V (V[r][c] = r^c, rows
/// = distinct evaluation points 0..N-1), then set M = V * (V_top)^-1 where
/// V_top is V's own first K rows. Multiplying by (V_top)^-1 on the right
/// forces M's first K rows to exactly the identity matrix (systematic: the
/// first K output shards equal the input shards unchanged) while
/// preserving the Vandermonde MDS property -- any K rows of M are still
/// linearly independent (invertible), because M's row-restriction to any
/// row subset S is V_S * (V_top)^-1, and V_S is invertible for any K
/// distinct evaluation points (classical Vandermonde determinant fact), and
/// multiplying an invertible matrix by another invertible matrix stays
/// invertible.
/// </summary>
public sealed class ReedSolomonCodec
{
    public int DataShards { get; }
    public int TotalShards { get; }
    public int ParityShards => TotalShards - DataShards;

    private readonly GF256Matrix _generator;

    public ReedSolomonCodec(int dataShards, int totalShards)
    {
        if (dataShards <= 0) throw new ArgumentOutOfRangeException(nameof(dataShards));
        if (totalShards < dataShards) throw new ArgumentOutOfRangeException(nameof(totalShards), "totalShards must be >= dataShards.");
        if (totalShards > 256) throw new ArgumentOutOfRangeException(nameof(totalShards), "GF(256) supports at most 256 shards.");

        DataShards = dataShards;
        TotalShards = totalShards;

        var vandermonde = GF256Matrix.Vandermonde(totalShards, dataShards);
        var top = vandermonde.SubMatrix(0, dataShards, 0, dataShards);
        _generator = vandermonde.Multiply(top.Invert());
    }

    /// <summary>
    /// Produces all N shards (first K are the data shards, unchanged;
    /// the remaining N-K are parity) from K equally-sized data shards.
    /// Callers (VideoPacketizer) are responsible for zero-padding the last
    /// data shard to match the others' length before calling this.
    /// </summary>
    public byte[][] EncodeParity(IReadOnlyList<byte[]> dataShards)
    {
        if (dataShards.Count != DataShards)
            throw new ArgumentException($"Expected {DataShards} data shards, got {dataShards.Count}.", nameof(dataShards));

        var shardLength = dataShards[0].Length;
        for (var i = 1; i < dataShards.Count; i++)
        {
            if (dataShards[i].Length != shardLength)
                throw new ArgumentException("All data shards must be the same length (pad before encoding).", nameof(dataShards));
        }

        var result = new byte[TotalShards][];
        for (var i = 0; i < DataShards; i++) result[i] = dataShards[i];

        for (var shardIndex = DataShards; shardIndex < TotalShards; shardIndex++)
        {
            var parity = new byte[shardLength];
            for (var k = 0; k < DataShards; k++)
            {
                var coefficient = _generator[shardIndex, k];
                if (coefficient == 0) continue;
                var source = dataShards[k];
                for (var b = 0; b < shardLength; b++)
                {
                    parity[b] ^= GF256.Multiply(coefficient, source[b]);
                }
            }
            result[shardIndex] = parity;
        }

        return result;
    }

    /// <summary>
    /// Reconstructs the K data shards from any K of the N shards.
    /// <paramref name="receivedShardIndices"/> and <paramref name="receivedShards"/>
    /// must have exactly DataShards entries, in matching order (index i in
    /// receivedShardIndices names which generator-matrix row
    /// receivedShards[i] corresponds to -- 0..DataShards-1 for an
    /// unmodified data shard, DataShards..TotalShards-1 for a parity
    /// shard). If all received indices are already < DataShards (no loss
    /// among the data shards), this still works but callers should prefer
    /// to skip calling Decode entirely in that case -- it's unnecessary
    /// matrix-inversion work.
    /// </summary>
    public byte[][] Decode(IReadOnlyList<int> receivedShardIndices, IReadOnlyList<byte[]> receivedShards)
    {
        if (receivedShardIndices.Count != DataShards || receivedShards.Count != DataShards)
            throw new ArgumentException($"Decode needs exactly {DataShards} shards to reconstruct.", nameof(receivedShards));

        var subGenerator = _generator.SelectRows(receivedShardIndices);
        var inverse = subGenerator.Invert();

        var shardLength = receivedShards[0].Length;
        var recovered = new byte[DataShards][];
        for (var i = 0; i < DataShards; i++) recovered[i] = new byte[shardLength];

        for (var outRow = 0; outRow < DataShards; outRow++)
        {
            var target = recovered[outRow];
            for (var inRow = 0; inRow < DataShards; inRow++)
            {
                var coefficient = inverse[outRow, inRow];
                if (coefficient == 0) continue;
                var source = receivedShards[inRow];
                for (var b = 0; b < shardLength; b++)
                {
                    target[b] ^= GF256.Multiply(coefficient, source[b]);
                }
            }
        }

        return recovered;
    }
}
