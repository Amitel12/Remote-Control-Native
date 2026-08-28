using RemoteControl.Net.Fec;
using Xunit;

namespace RemoteControl.Net.Tests.Fec;

public class ReedSolomonCodecTests
{
    [Fact]
    public void EncodeParity_FirstKShards_AreUnchangedFromInput_SystematicProperty()
    {
        var codec = new ReedSolomonCodec(dataShards: 4, totalShards: 8);
        var data = MakeRandomShards(4, 16, seed: 1);

        var all = codec.EncodeParity(data);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(data[i], all[i]);
        }
        Assert.Equal(8, all.Length);
    }

    [Fact]
    public void Decode_FromAnyKOfNShards_ExhaustivelyReconstructsOriginalData()
    {
        // The whole point of Reed-Solomon erasure coding: ANY K of the N
        // shards must reconstruct the original K data shards, not just
        // some convenient subset. Brute-force every C(N,K) combination for
        // small N to prove the MDS property actually holds for this
        // implementation, not just for the one loss pattern a single test
        // happens to pick.
        const int dataShards = 3;
        const int totalShards = 6;
        var codec = new ReedSolomonCodec(dataShards, totalShards);
        var original = MakeRandomShards(dataShards, 32, seed: 42);
        var allShards = codec.EncodeParity(original);

        foreach (var combination in Combinations(Enumerable.Range(0, totalShards).ToArray(), dataShards))
        {
            var received = combination.Select(i => allShards[i]).ToArray();
            var recovered = codec.Decode(combination, received);

            for (var i = 0; i < dataShards; i++)
            {
                Assert.True(original[i].AsSpan().SequenceEqual(recovered[i]),
                    $"Reconstruction failed using shard indices [{string.Join(",", combination)}]");
            }
        }
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(4, 4)]   // ParityShards == 0: no redundancy, but must still encode/decode correctly.
    [InlineData(4, 10)]
    [InlineData(10, 20)]
    public void RoundTrips_ForVariousShapes_WithRandomLossPattern(int dataShards, int totalShards)
    {
        var codec = new ReedSolomonCodec(dataShards, totalShards);
        var original = MakeRandomShards(dataShards, 64, seed: dataShards * 1000 + totalShards);
        var allShards = codec.EncodeParity(original);

        // Simulate losing (totalShards - dataShards) shards -- the maximum
        // recoverable loss -- picked pseudo-randomly rather than always the
        // same "first N-K" pattern, so this isn't accidentally only
        // exercising an easy case.
        var rng = new Random(dataShards * 7 + totalShards * 13);
        var order = Enumerable.Range(0, totalShards).OrderBy(_ => rng.Next()).ToArray();
        var keptIndices = order.Take(dataShards).OrderBy(i => i).ToArray();
        var keptShards = keptIndices.Select(i => allShards[i]).ToArray();

        var recovered = codec.Decode(keptIndices, keptShards);

        for (var i = 0; i < dataShards; i++)
        {
            Assert.True(original[i].AsSpan().SequenceEqual(recovered[i]));
        }
    }

    [Fact]
    public void Decode_WithFewerThanKShards_Throws()
    {
        var codec = new ReedSolomonCodec(dataShards: 4, totalShards: 8);
        var data = MakeRandomShards(4, 16, seed: 2);
        var allShards = codec.EncodeParity(data);

        Assert.Throws<ArgumentException>(() => codec.Decode(new[] { 0, 1, 2 }, new[] { allShards[0], allShards[1], allShards[2] }));
    }

    [Fact]
    public void Constructor_RejectsTotalShardsLessThanDataShards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomonCodec(dataShards: 5, totalShards: 4));
    }

    private static byte[][] MakeRandomShards(int count, int shardLength, int seed)
    {
        var rng = new Random(seed);
        var shards = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            shards[i] = new byte[shardLength];
            rng.NextBytes(shards[i]);
        }
        return shards;
    }

    private static IEnumerable<int[]> Combinations(int[] items, int k)
    {
        var indices = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return indices.Select(i => items[i]).ToArray();

            var pivot = k - 1;
            while (pivot >= 0 && indices[pivot] == items.Length - k + pivot) pivot--;
            if (pivot < 0) yield break;

            indices[pivot]++;
            for (var j = pivot + 1; j < k; j++) indices[j] = indices[j - 1] + 1;
        }
    }
}
