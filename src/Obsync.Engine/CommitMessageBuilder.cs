using System.Globalization;
using System.Text;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine;

/// <summary>Builds the deterministic commit subject and body for a run.</summary>
internal static class CommitMessageBuilder
{
    private const int MaxListedPerCategory = 50;

    /// <summary>
    /// Character budget for the commit subject, which is also used verbatim as the pull request
    /// title. GitHub documents no title limit, but it rejects over-long issue and pull request
    /// titles with a 422 instead of truncating them (its sibling body limit of 65,536 is well
    /// attested by real 422s). The widely reported title figure is 256; this leaves headroom under
    /// it so the exact number does not have to be right for the subject to be accepted.
    /// </summary>
    private const int MaxSubjectLength = 250;

    /// <summary>Databases named in full in the subject before the remainder is summarized.</summary>
    private const int MaxNamedDatabasesInSubject = 3;

    /// <summary>Databases named in full on the body's <c>Database:</c> line.</summary>
    private const int MaxNamedDatabasesInBody = 50;

    /// <summary>
    /// What the subject costs outside the scope, server and timestamp — derived from the format
    /// itself so it cannot drift if the wording changes.
    /// </summary>
    private static readonly int SubjectOverhead = FormatSubject(string.Empty, string.Empty, string.Empty).Length;

    /// <param name="databases">
    /// The databases this run actually covered, in order. Used to summarize the scope rather than
    /// re-splitting <see cref="SyncRun.Databases"/> — a SQL Server identifier may legally contain
    /// the ", " that joined it, so splitting the joined string would miscount.
    /// </param>
    public static (string Subject, string Body) Build(
        SyncRun run, SyncJob job, IReadOnlyList<ObjectChange> changes, IReadOnlyList<string> databases)
    {
        // Invariant calendar: commit subjects must not vary with the executing account's culture.
        var stamp = run.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var fullScope = string.IsNullOrEmpty(run.Databases) ? job.Name : run.Databases;
        var subject = FormatSubject(FitScope(fullScope, databases, run.ServerName, stamp), run.ServerName, stamp);

        // Backstop: a pathological server name or single database name can exhaust the budget on its
        // own, leaving FitScope nothing to give back. No input may produce an over-long subject.
        subject = Clamp(subject, MaxSubjectLength);

        var body = new StringBuilder();
        body.Append("Server: ").Append(run.ServerName).Append('\n');
        body.Append("Database: ").Append(FitDatabaseList(run.Databases, databases)).Append('\n');
        body.Append("Job: ").Append(job.Name).Append('\n');
        body.Append("Run ID: ").Append(run.RunKey).Append('\n');
        body.Append("Objects scanned: ").Append(run.ObjectsScanned.ToString("N0")).Append('\n');
        body.Append("Added: ").Append(run.ObjectsAdded).Append('\n');
        body.Append("Modified: ").Append(run.ObjectsModified).Append('\n');
        body.Append("Deleted: ").Append(run.ObjectsDeleted).Append('\n');
        if (run.ObjectsRestored > 0)
        {
            body.Append("Restored: ").Append(run.ObjectsRestored).Append('\n');
        }

        body.Append("Duration: ").Append(TimeSpan.FromMilliseconds(run.DurationMs).ToString(@"hh\:mm\:ss")).Append('\n');

        // One pass over the changes (a VLDB run can carry 500k) bucketing per category; each bucket
        // retains only the paths it will actually print, so nothing near the full set is ever sorted.
        var modified = new CategoryBucket();
        var added = new CategoryBucket();
        var deleted = new CategoryBucket();
        foreach (var change in changes)
        {
            var bucket = change.ChangeType switch
            {
                ChangeType.Modified => modified,
                ChangeType.Added => added,
                ChangeType.Deleted => deleted,
                _ => null,
            };
            bucket?.Add(change.RelativePath);
        }

        AppendCategory(body, "Modified", modified);
        AppendCategory(body, "Added", added);
        AppendCategory(body, "Deleted", deleted);

        return (subject, body.ToString().TrimEnd('\n'));
    }

    private static string FormatSubject(string scope, string serverName, string stamp) =>
        $"[{scope}] SQL object changes from {serverName} - {stamp}";

    /// <summary>
    /// Returns the scope segment shortened to whatever the subject can still afford. A list that
    /// already fits is returned untouched, so ordinary single-database jobs keep the exact subject
    /// they have always had; only a list that would push the subject over the budget is summarized.
    /// The fixed tail (server and timestamp) is what makes a subject scannable in a log or a pull
    /// request list, so the scope yields rather than the tail.
    /// </summary>
    private static string FitScope(
        string fullScope, IReadOnlyList<string> databases, string serverName, string stamp)
    {
        var budget = MaxSubjectLength - SubjectOverhead - serverName.Length - stamp.Length;

        if (fullScope.Length <= budget)
        {
            return fullScope;
        }

        // Name as many databases as will fit, then account for the rest. Capped at Count - 1 so
        // there is always a remainder to report.
        for (var named = Math.Min(MaxNamedDatabasesInSubject, databases.Count - 1); named >= 1; named--)
        {
            var candidate = $"{string.Join(", ", databases.Take(named))} +{databases.Count - named} more";
            if (candidate.Length <= budget)
            {
                return candidate;
            }
        }

        // Not even one name fits. With several databases the count is the most useful thing left;
        // with one (an enormous identifier) the truncated name still says more than "1 databases".
        var counted = databases.Count > 1
            ? $"{databases.Count} databases"
            : fullScope;

        return Clamp(counted, Math.Max(budget, 0));
    }

    /// <summary>
    /// The body carries the authoritative list, bounded the same way the file lists are so a server
    /// with hundreds of databases cannot push the pull request body toward GitHub's 65,536 limit.
    /// </summary>
    private static string FitDatabaseList(string fullList, IReadOnlyList<string> databases) =>
        databases.Count <= MaxNamedDatabasesInBody
            ? fullList
            : $"{string.Join(", ", databases.Take(MaxNamedDatabasesInBody))} … and {databases.Count - MaxNamedDatabasesInBody} more";

    /// <summary>
    /// Truncates to <paramref name="max"/> characters with a trailing ellipsis, never splitting a
    /// surrogate pair — SQL Server identifiers are nvarchar and may contain non-BMP characters.
    /// </summary>
    private static string Clamp(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        if (max <= 0)
        {
            return string.Empty;
        }

        if (max == 1)
        {
            return "…";
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }

    private static void AppendCategory(StringBuilder body, string title, CategoryBucket bucket)
    {
        if (bucket.Count == 0)
        {
            return;
        }

        body.Append('\n').Append(title).Append(":\n");
        foreach (var path in bucket.SmallestPaths)
        {
            body.Append("  - ").Append(path).Append('\n');
        }

        if (bucket.Count > MaxListedPerCategory)
        {
            body.Append("  … and ").Append(bucket.Count - MaxListedPerCategory).Append(" more\n");
        }
    }

    /// <summary>
    /// One category's tally: the total count plus the <see cref="MaxListedPerCategory"/> ordinally
    /// smallest paths — exactly what "sort by path, list the first 50, then '… and N more'" prints —
    /// kept via bounded insertion instead of sorting the whole category.
    /// </summary>
    private sealed class CategoryBucket
    {
        private readonly List<string> _smallest = new(MaxListedPerCategory + 1);

        public int Count { get; private set; }

        public IReadOnlyList<string> SmallestPaths => _smallest;

        public void Add(string path)
        {
            Count++;
            if (_smallest.Count == MaxListedPerCategory
                && StringComparer.Ordinal.Compare(path, _smallest[^1]) >= 0)
            {
                return;
            }

            var index = _smallest.BinarySearch(path, StringComparer.Ordinal);
            _smallest.Insert(index < 0 ? ~index : index, path);
            if (_smallest.Count > MaxListedPerCategory)
            {
                _smallest.RemoveAt(MaxListedPerCategory);
            }
        }
    }
}
