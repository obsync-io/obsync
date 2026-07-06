using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffChangeType = DiffPlex.DiffBuilder.Model.ChangeType;

namespace Obsync.App.ViewModels;

/// <summary>How a diff line reads: added, deleted, unchanged, or a filler slot in a split pane.</summary>
public enum DiffRowKind
{
    Unchanged,
    Added,
    Deleted,

    /// <summary>A placeholder that keeps the split panes aligned where the other side has a line.</summary>
    Imaginary,
}

/// <summary>A run of characters within a diff line; emphasized runs are intra-line word changes.</summary>
public sealed record DiffSegment(string Text, bool IsEmphasized);

/// <summary>One renderable diff line. <see cref="IsStruck"/> strikes the text (deleted-object view).</summary>
public sealed record DiffRow(DiffRowKind Kind, int? LineNumber, IReadOnlyList<DiffSegment> Segments, bool IsStruck = false);

/// <summary>
/// Maps DiffPlex output to the lightweight <see cref="DiffRow"/> records the diff viewer binds to,
/// so the view never depends on DiffPlex types. Pure and synchronous; safe to run off the UI thread.
/// </summary>
public static class DiffRowMapper
{
    /// <summary>Side-by-side rows. Both lists have the same count (filler rows keep them aligned).</summary>
    public static (IReadOnlyList<DiffRow> Old, IReadOnlyList<DiffRow> New) BuildSplit(string oldText, string newText)
    {
        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(Normalize(oldText), Normalize(newText), false);
        return (Map(model.OldText.Lines, isOldPane: true), Map(model.NewText.Lines, isOldPane: false));
    }

    /// <summary>Unified rows: deletions inline above their replacements, no filler rows.</summary>
    public static IReadOnlyList<DiffRow> BuildUnified(string oldText, string newText)
    {
        var model = InlineDiffBuilder.Instance.BuildDiffModel(Normalize(oldText), Normalize(newText), false);
        var rows = new List<DiffRow>(model.Lines.Count);
        foreach (var line in model.Lines)
        {
            if (line.Type == DiffChangeType.Imaginary)
            {
                continue;
            }

            var kind = line.Type switch
            {
                DiffChangeType.Inserted => DiffRowKind.Added,
                DiffChangeType.Deleted => DiffRowKind.Deleted,
                _ => DiffRowKind.Unchanged,
            };
            rows.Add(new DiffRow(kind, line.Position, [new DiffSegment(line.Text ?? string.Empty, false)]));
        }

        return rows;
    }

    /// <summary>Full content as numbered rows of one kind — the added/deleted single-pane views.</summary>
    public static IReadOnlyList<DiffRow> BuildFullContent(string text, DiffRowKind kind, bool struck = false)
    {
        var lines = Normalize(text).TrimEnd('\n').Split('\n');
        if (lines is [""])
        {
            return [];
        }

        var rows = new List<DiffRow>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            rows.Add(new DiffRow(kind, i + 1, [new DiffSegment(lines[i], false)], struck));
        }

        return rows;
    }

    private static IReadOnlyList<DiffRow> Map(IReadOnlyList<DiffPiece> lines, bool isOldPane)
    {
        var rows = new List<DiffRow>(lines.Count);
        foreach (var line in lines)
        {
            var kind = line.Type switch
            {
                DiffChangeType.Unchanged => DiffRowKind.Unchanged,
                DiffChangeType.Imaginary => DiffRowKind.Imaginary,
                // A "Modified" line reads as deleted on the old side and added on the new side.
                _ => isOldPane ? DiffRowKind.Deleted : DiffRowKind.Added,
            };
            rows.Add(new DiffRow(kind, line.Position, SegmentsOf(line)));
        }

        return rows;
    }

    private static IReadOnlyList<DiffSegment> SegmentsOf(DiffPiece line)
    {
        // Word-level sub-pieces exist only on Modified lines from the side-by-side builder.
        if (line.Type != DiffChangeType.Modified || line.SubPieces is not { Count: > 0 } subPieces)
        {
            return [new DiffSegment(line.Text ?? string.Empty, false)];
        }

        var segments = new List<DiffSegment>(subPieces.Count);
        foreach (var sub in subPieces)
        {
            if (string.IsNullOrEmpty(sub.Text))
            {
                continue; // imaginary word slots carry no text
            }

            segments.Add(new DiffSegment(sub.Text, sub.Type is not (DiffChangeType.Unchanged or DiffChangeType.Imaginary)));
        }

        return segments.Count > 0 ? segments : [new DiffSegment(line.Text ?? string.Empty, false)];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
