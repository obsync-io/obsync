namespace Obsync.Shared.Models;

/// <summary>
/// Parsing and production-classification for a job's free-form environment tags. Pure and shared by
/// the wizard (entry), the production run guard, and chip rendering.
/// </summary>
public static class JobTags
{
    /// <summary>
    /// Parses a comma-separated tag field into a normalized list: trimmed, blanks dropped, de-duped
    /// case-insensitively, original order preserved.
    /// </summary>
    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var tag in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    /// <summary>True when any of <paramref name="tags"/> matches a production marker (case-insensitive).</summary>
    public static bool IsProduction(IEnumerable<string> tags, IEnumerable<string> markers)
    {
        var set = MarkerSet(markers);
        return set.Count != 0 && tags.Any(t => set.Contains(t.Trim()));
    }

    /// <summary>Classifies each tag into a <see cref="TagChip"/>, flagging the ones that match a production marker.</summary>
    public static List<TagChip> Classify(IReadOnlyList<string> tags, IEnumerable<string> markers)
    {
        var set = MarkerSet(markers);
        return [.. tags.Select(t => new TagChip(t, set.Contains(t.Trim())))];
    }

    private static HashSet<string> MarkerSet(IEnumerable<string> markers) =>
        new(markers.Select(m => m.Trim()).Where(m => m.Length != 0), StringComparer.OrdinalIgnoreCase);
}
