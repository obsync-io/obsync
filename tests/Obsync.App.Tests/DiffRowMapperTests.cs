using Obsync.App.ViewModels;

namespace Obsync.App.Tests;

/// <summary>Verifies the DiffPlex → <see cref="DiffRow"/> mapping the diff viewer binds to.</summary>
public sealed class DiffRowMapperTests
{
    [Fact]
    public void BuildSplit_MarksAModifiedLine_DeletedOldSide_AddedNewSide_WithWordEmphasis()
    {
        var (oldRows, newRows) = DiffRowMapper.BuildSplit(
            "SELECT 1 AS a\nFROM t;", "SELECT 2 AS a\nFROM t;");

        Assert.Equal(oldRows.Count, newRows.Count);

        Assert.Equal(DiffRowKind.Deleted, oldRows[0].Kind);
        Assert.Equal(DiffRowKind.Added, newRows[0].Kind);
        Assert.Equal(1, oldRows[0].LineNumber);
        Assert.Equal(1, newRows[0].LineNumber);

        // Only the changed word carries the stronger intra-line emphasis.
        Assert.Contains(oldRows[0].Segments, s => s.IsEmphasized && s.Text.Contains('1'));
        Assert.Contains(newRows[0].Segments, s => s.IsEmphasized && s.Text.Contains('2'));
        Assert.Contains(oldRows[0].Segments, s => !s.IsEmphasized);

        Assert.Equal(DiffRowKind.Unchanged, oldRows[1].Kind);
        Assert.Equal(DiffRowKind.Unchanged, newRows[1].Kind);
    }

    [Fact]
    public void BuildSplit_PadsTheOldPaneWithFillerRows_WhereLinesWereAdded()
    {
        var (oldRows, newRows) = DiffRowMapper.BuildSplit("line one", "line one\nline two");

        Assert.Equal(2, oldRows.Count);
        Assert.Equal(2, newRows.Count);
        Assert.Equal(DiffRowKind.Imaginary, oldRows[1].Kind);
        Assert.Null(oldRows[1].LineNumber);
        Assert.Equal(DiffRowKind.Added, newRows[1].Kind);
        Assert.Equal(2, newRows[1].LineNumber);
    }

    [Fact]
    public void BuildUnified_ListsDeletionsAndInsertionsInline_WithoutFillerRows()
    {
        var rows = DiffRowMapper.BuildUnified("a\nold line\nc", "a\nnew line\nc");

        Assert.Equal(
            new[] { DiffRowKind.Unchanged, DiffRowKind.Deleted, DiffRowKind.Added, DiffRowKind.Unchanged },
            rows.Select(r => r.Kind).ToArray());
        Assert.Equal("old line", rows[1].Segments.Single().Text);
        Assert.Equal("new line", rows[2].Segments.Single().Text);
    }

    [Fact]
    public void BuildFullContent_NumbersEveryLine_WithTheRequestedKindAndStrike()
    {
        var rows = DiffRowMapper.BuildFullContent("DROP VIEW dbo.v;\r\nGO\r\n", DiffRowKind.Deleted, struck: true);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffRowKind.Deleted, r.Kind));
        Assert.All(rows, r => Assert.True(r.IsStruck));
        Assert.Equal([1, 2], rows.Select(r => r.LineNumber!.Value).ToArray());
        Assert.Equal("DROP VIEW dbo.v;", rows[0].Segments.Single().Text);
        Assert.Equal("GO", rows[1].Segments.Single().Text);
    }

    [Fact]
    public void BuildFullContent_OfEmptyText_ProducesNoRows()
    {
        Assert.Empty(DiffRowMapper.BuildFullContent(string.Empty, DiffRowKind.Unchanged));
    }
}
