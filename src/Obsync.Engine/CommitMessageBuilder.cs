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
        var subject = $"[{scope}] SQL object changes from {run.ServerName} - {run.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm}";

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

        AppendCategory(body, "Modified", changes, ChangeType.Modified);
        AppendCategory(body, "Added", changes, ChangeType.Added);
        AppendCategory(body, "Deleted", changes, ChangeType.Deleted);

        return (subject, body.ToString().TrimEnd('\n'));
    }

    private static void AppendCategory(StringBuilder body, string title, IReadOnlyList<ObjectChange> changes, ChangeType type)
    {
        var matching = changes.Where(c => c.ChangeType == type).ToList();
        if (matching.Count == 0)
        {
            return;
        }

        body.Append('\n').Append(title).Append(":\n");
        foreach (var change in matching.Take(MaxListedPerCategory))
        {
            body.Append("  - ").Append(change.RelativePath).Append('\n');
        }

        if (matching.Count > MaxListedPerCategory)
        {
            body.Append("  … and ").Append(matching.Count - MaxListedPerCategory).Append(" more\n");
        }
    }
}
