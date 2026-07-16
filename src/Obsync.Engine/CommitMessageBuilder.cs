using System.Globalization;
using System.Text;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine;

/// <summary>Builds the deterministic commit subject and body for a run.</summary>
internal static class CommitMessageBuilder
{
    private const int MaxListedPerCategory = 50;

    public static (string Subject, string Body) Build(SyncRun run, SyncJob job, IReadOnlyList<ObjectChange> changes)
    {
        var scope = string.IsNullOrEmpty(run.Databases) ? job.Name : run.Databases;
        // Invariant calendar: commit subjects must not vary with the executing account's culture.
        var stamp = run.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var subject = $"[{scope}] SQL object changes from {run.ServerName} - {stamp}";

        var body = new StringBuilder();
        body.Append("Server: ").Append(run.ServerName).Append('\n');
        body.Append("Database: ").Append(run.Databases).Append('\n');
        body.Append("Job: ").Append(job.Name).Append('\n');
        body.Append("Run ID: ").Append(run.RunKey).Append('\n');
        body.Append("Objects scanned: ").Append(run.ObjectsScanned.ToString("N0")).Append('\n');
        body.Append("Added: ").Append(run.ObjectsAdded).Append('\n');
        body.Append("Modified: ").Append(run.ObjectsModified).Append('\n');
        body.Append("Deleted: ").Append(run.ObjectsDeleted).Append('\n');
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
