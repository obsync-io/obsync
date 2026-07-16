using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Severity of a "Needs attention" row. Drives the row's status dot colour only — the
/// row text always states the problem, so the meaning is never carried by colour alone.</summary>
public enum AttentionSeverity
{
    Error,
    Warning,
}

/// <summary>
/// One row of the dashboard's "Needs attention" card. <see cref="JobId"/> is null for server rows,
/// whose action navigates to the Servers section instead of a job.
/// </summary>
public sealed record AttentionItem(AttentionSeverity Severity, string Text, string ActionLabel, Guid? JobId);

/// <summary>
/// Pure aggregation behind the dashboard's "Needs attention" card: failed and warning last runs,
/// overdue schedules, and servers whose last connection test failed. Free of I/O and clocks so the
/// row set is directly testable; the view model caps the rendered rows and adds the "+N more" line.
/// </summary>
internal static class AttentionModel
{
    /// <summary>How many rows the card renders before collapsing the rest into "+N more".</summary>
    public const int MaxRows = 6;

    /// <param name="runErrors">Error messages of recently loaded runs, keyed by run id — lets a
    /// failed row quote the failure without a per-job query (rows whose run fell outside the
    /// recent window simply omit the quote).</param>
    public static IReadOnlyList<AttentionItem> Build(
        IReadOnlyList<SyncJob> jobs,
        IReadOnlyList<SqlConnectionProfile> servers,
        IReadOnlyDictionary<Guid, string> runErrors,
        DateTimeOffset now)
    {
        var items = new List<AttentionItem>();

        foreach (var job in jobs.Where(j => j.RunSummary.LastStatus == RunStatus.Failed))
        {
            var error = job.RunSummary.LastRunId is { } runId && runErrors.TryGetValue(runId, out var message)
                ? FirstLine(message)
                : null;
            items.Add(new(AttentionSeverity.Error,
                error is null ? $"Job “{job.Name}” failed" : $"Job “{job.Name}” failed — {error}",
                "Open", job.Id));
        }

        foreach (var job in jobs.Where(j => j.RunSummary.LastStatus == RunStatus.Warning))
        {
            items.Add(new(AttentionSeverity.Warning, $"Job “{job.Name}” completed with warnings", "Open", job.Id));
        }

        foreach (var job in jobs.Where(j => j.IsScheduleOverdue(now)))
        {
            items.Add(new(AttentionSeverity.Warning, $"Job “{job.Name}” missed its scheduled run", "Open", job.Id));
        }

        foreach (var server in servers.Where(s => s.LastTestStatus == ConnectionTestStatus.Failed))
        {
            items.Add(new(AttentionSeverity.Error,
                $"Server “{server.Name}” failed its last connection test", "Open Servers", null));
        }

        return items;
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return (end < 0 ? message : message[..end]).Trim();
    }
}
