using Obsync.App.ViewModels;

namespace Obsync.App.Tests;

/// <summary>The diff viewer's find logic: which rows match a query, and the wrap-around stepping.</summary>
public sealed class DiffTextSearchTests
{
    private static DiffRow Row(params string[] segments) =>
        new(DiffRowKind.Unchanged, 1, [.. segments.Select(s => new DiffSegment(s, false))]);

    [Fact]
    public void FindMatches_ReturnsRowIndexesInOrder_CaseInsensitively()
    {
        IReadOnlyList<DiffRow> rows =
        [
            Row("CREATE VIEW dbo.vw_Orders AS"),
            Row("SELECT Id, Total"),
            Row("FROM dbo.Orders;"),
        ];

        Assert.Equal([0, 2], DiffTextSearch.FindMatches(rows, "orders"));
        Assert.Empty(DiffTextSearch.FindMatches(rows, "no such text"));
    }

    [Fact]
    public void FindMatches_SeesTextThatSpansIntraLineSegments()
    {
        // Word-level diffs split a line into emphasized/plain runs; the match must not care.
        IReadOnlyList<DiffRow> rows = [Row("SELECT ", "Total", " FROM t")];

        Assert.Equal([0], DiffTextSearch.FindMatches(rows, "select total"));
    }

    [Fact]
    public void FindMatches_MatchesNothing_ForAnEmptyQuery() =>
        Assert.Empty(DiffTextSearch.FindMatches([Row("anything")], string.Empty));

    [Fact]
    public void NextAndPrevious_WrapAroundTheMatchList()
    {
        Assert.Equal(1, DiffTextSearch.NextPosition(count: 3, position: 0));
        Assert.Equal(0, DiffTextSearch.NextPosition(count: 3, position: 2)); // wraps to the first
        Assert.Equal(2, DiffTextSearch.PreviousPosition(count: 3, position: 0)); // wraps to the last
        Assert.Equal(1, DiffTextSearch.PreviousPosition(count: 3, position: 2));
    }

    [Fact]
    public void NextAndPrevious_ReportNoPosition_WithoutMatches()
    {
        Assert.Equal(-1, DiffTextSearch.NextPosition(count: 0, position: -1));
        Assert.Equal(-1, DiffTextSearch.PreviousPosition(count: 0, position: -1));
    }
}
