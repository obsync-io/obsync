using Obsync.Shared.Objects;
using Obsync.Smo;

namespace Obsync.Engine.Tests;

/// <summary>
/// The pure arithmetic behind parallel SMO scripting: slice-count bounds, slice sizing, and the
/// memory budget that decides whether a collection is bulk-prefetched or scripted lazily.
/// </summary>
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

    [Fact]
    public void PrefetchBudget_IsAboutWhatPrefetchLoads_NotWhatTheRunScripts()
    {
        // The gate compared the FILTERED work list against a ceiling whose own comment describes the
        // cost of prefetching every table in the DATABASE — PrefetchObjects takes a database and a
        // CLR type and has no overload that narrows it. So a schema filter selecting 400 tables out
        // of 500k passed a ceiling of 25k and then bulk-loaded all 500k, on every slice connection.
        // This pins the two numbers apart so they cannot be conflated again.
        const int inCollection = 500_000;
        const int selected = 400;

        Assert.True(SmoPrefetchBudget.Fits(selected, sliceCount: 8), "the filtered count would have allowed prefetch");
        Assert.False(SmoPrefetchBudget.Fits(inCollection, sliceCount: 8), "the collection size must refuse it");
    }

    [Fact]
    public void PrefetchBudget_BoundsTheSamePeakTheOldCeilingSanctioned()
    {
        // The budget replaced a bare 25,000-object ceiling, and it is deliberately set to exactly
        // what that ceiling already permitted at the engine's maximum scripting fan-out of 8
        // connections. That equivalence is the whole licence for the change: it spends the same
        // peak more honestly, it does not raise it. If someone edits the budget, this fails and
        // they have to justify pinning more memory rather than doing it by accident.
        Assert.Equal(SmoPrefetchBudget.BudgetBytes, SmoPrefetchBudget.EstimateBytes(25_000, sliceCount: 8));
        Assert.True(SmoPrefetchBudget.Fits(25_000, sliceCount: 8));
        Assert.False(SmoPrefetchBudget.Fits(25_001, sliceCount: 8));
    }

    [Theory]
    // Each slice connection caches its OWN copy of the prefetched child metadata, so the object
    // limit falls as the run fans out. The old ceiling ignored the fan-out entirely and allowed the
    // same 25,000 objects whether they were about to be prefetched onto one connection or eight —
    // pricing a 175 MB sequential prefetch identically to a 1.4 GB eight-way one, and refusing
    // 100,000 objects on a single connection (700 MB) that it would happily have allowed as 12,500
    // objects across eight (also 700 MB).
    [InlineData(200_000, 1, true)]
    [InlineData(200_001, 1, false)]
    [InlineData(100_000, 2, true)]
    [InlineData(100_001, 2, false)]
    [InlineData(50_000, 4, true)]
    [InlineData(50_001, 4, false)]
    [InlineData(100_000, 8, false)]  // the VLDB case the cliff is really about: still refused
    public void PrefetchBudget_ScalesWithTheFanOutThatDuplicatesIt(int inCollection, int sliceCount, bool fits)
    {
        Assert.Equal(fits, SmoPrefetchBudget.Fits(inCollection, sliceCount));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void MaxObjects_IsTheLargestCollectionThatStillFits(int sliceCount)
    {
        var max = SmoPrefetchBudget.MaxObjects(sliceCount);

        Assert.True(SmoPrefetchBudget.Fits(max, sliceCount), $"{max:N0} objects should fit at {sliceCount} slices");
        Assert.False(SmoPrefetchBudget.Fits(max + 1, sliceCount), $"{max + 1:N0} objects should not fit at {sliceCount} slices");
    }

    [Fact]
    public void PrefetchBudget_TreatsNoFanOutAsOneConnection()
    {
        // ComputeSliceCount never returns 0, but the sequential branch passes a literal 1 and a
        // future caller could pass the raw parallelism, which the engine allows to be 0. Prefetch
        // still costs one copy in that case; charging zero would make the budget infinite.
        Assert.Equal(SmoPrefetchBudget.EstimateBytes(1_000, 1), SmoPrefetchBudget.EstimateBytes(1_000, 0));
        Assert.Equal(SmoPrefetchBudget.MaxObjects(1), SmoPrefetchBudget.MaxObjects(0));
    }

    [Fact]
    public void PrefetchLimit_MessageSaysWhatHappenedAndThatOutputIsUnaffected()
    {
        // The message IS the seam: Obsync.Smo has no run log, so the caller records this sentence
        // verbatim. It has to stand alone in a run log — name the database and type, say scripting
        // is slower and not wrong, and give the number the reader would otherwise have to guess.
        var limit = new SmoPrefetchLimit(
            "Warehouse", SqlObjectType.Table, 120_000, 8,
            SmoPrefetchBudget.EstimateBytes(120_000, 8), SmoPrefetchBudget.BudgetBytes,
            SmoPrefetchBudget.MaxObjects(8));

        var message = limit.Message;

        Assert.Contains("Warehouse", message, StringComparison.Ordinal);
        Assert.Contains("Table", message, StringComparison.Ordinal);
        Assert.Contains("120,000", message, StringComparison.Ordinal);
        Assert.Contains("identical output", message, StringComparison.Ordinal);
        Assert.Contains("25,000", message, StringComparison.Ordinal); // what would still fit at 8 slices
    }
}
