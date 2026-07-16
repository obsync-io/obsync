namespace Obsync.App.ViewModels;

/// <summary>
/// Row-level find for the diff viewer: which rows match a query, and wrap-around next/previous
/// stepping through them. Pure, so the navigation logic is unit-tested directly.
/// </summary>
public static class DiffTextSearch
{
    /// <summary>Indexes of the rows whose text contains <paramref name="query"/> (case-insensitive),
    /// in row order. An empty query matches nothing.</summary>
    public static IReadOnlyList<int> FindMatches(IReadOnlyList<DiffRow> rows, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        var matches = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var text = rows[i].Segments.Count == 1
                ? rows[i].Segments[0].Text
                : string.Concat(rows[i].Segments.Select(s => s.Text));
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    /// <summary>The next position in a match list of <paramref name="count"/> entries, wrapping to
    /// the first after the last; −1 when there are no matches.</summary>
    public static int NextPosition(int count, int position) => count == 0 ? -1 : (position + 1) % count;

    /// <summary>The previous position, wrapping to the last before the first; −1 when there are no matches.</summary>
    public static int PreviousPosition(int count, int position) =>
        count == 0 ? -1 : position <= 0 ? count - 1 : position - 1;
}
