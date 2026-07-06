using Obsync.Smo;

namespace Obsync.Engine.Tests;

/// <summary>The pure partitioning math behind parallel SMO scripting: slice-count bounds and slice sizing.</summary>
public sealed class SmoPartitioningTests
{
    [Theory]
    [InlineData(0, 8, 1)]     // nothing to do
    [InlineData(31, 8, 1)]    // below the per-slice minimum → sequential
    [InlineData(500, 1, 1)]   // parallelism off → sequential
    [InlineData(500, 0, 1)]
    [InlineData(32, 8, 1)]    // 32/32 = 1 slice
    [InlineData(64, 8, 2)]    // count/32 caps the fan-out for smallish lists
    [InlineData(100_000, 8, 8)] // parallelism caps large lists
    [InlineData(100_000, 4, 4)]
    public void ComputeSliceCount_BoundsBySizeAndParallelism(int itemCount, int parallelism, int expected)
    {
        Assert.Equal(expected, SmoScriptProvider.ComputeSliceCount(itemCount, parallelism));
    }

    [Theory]
    [InlineData(100, 8)]
    [InlineData(100, 7)]  // uneven remainder
    [InlineData(64, 2)]
    [InlineData(33, 1)]
    public void PartitionSlices_AreContiguous_CoverEverything_AndDifferByAtMostOne(int itemCount, int sliceCount)
    {
        var slices = SmoScriptProvider.PartitionSlices(itemCount, sliceCount);

        Assert.Equal(sliceCount, slices.Count);
        Assert.Equal(itemCount, slices.Sum(s => s.Count));

        var expectedOffset = 0;
        foreach (var (offset, count) in slices)
        {
            Assert.Equal(expectedOffset, offset);
            expectedOffset += count;
        }

        Assert.True(slices.Max(s => s.Count) - slices.Min(s => s.Count) <= 1);
    }
}
