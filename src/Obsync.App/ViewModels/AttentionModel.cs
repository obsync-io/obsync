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
/// One row of the dashboard's "Needs attention" card. <see cref="JobId"/> is null for rows that are
/// not about one job; those navigate to <see cref="Section"/> instead.
/// </summary>
/// <param name="Section">
/// Shell section to open when <paramref name="JobId"/> is null. Defaults to Servers, which was the
/// hardcoded destination back when a failed connection test was the only job-less row.
/// </param>
public sealed record AttentionItem(
    AttentionSeverity Severity, string Text, string ActionLabel, Guid? JobId, string Section = "Servers");

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
    /// <param name="skippedOccurrences">
    /// Reason text per job whose most recent run was a skipped occurrence. A skip deliberately does
    /// not touch the job's run summary — it is not a run outcome — so this is the only state that
    /// can surface it. The overdue rule cannot: reconcile keeps the next-run time in the future, so
    /// a job that quietly lost an occurrence still looks perfectly healthy.
    /// </param>
    /// <param name="alertFailure">
    /// The last alert Obsync could not deliver, or null when the most recent attempt succeeded.
    /// Alert sends are best-effort by design, so nothing else in the product can report one: the
    /// run itself still succeeds, and the Settings test button sends from the app under the
    /// signed-in user, which is precisely the identity that is NOT failing when the service's
    /// account cannot read the SMTP password.
    /// </param>
    public static IReadOnlyList<AttentionItem> Build(
        IReadOnlyList<SyncJob> jobs,
        IReadOnlyList<SqlConnectionProfile> servers,
        IReadOnlyList<GitRepositoryProfile> repositories,
        IReadOnlyDictionary<Guid, string> runErrors,
        IReadOnlyDictionary<Guid, string> skippedOccurrences,
        DateTimeOffset now,
        AlertDeliveryFailure? alertFailure = null)
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

        // Ahead of the overdue row, and excluded from it: a schedule its window can never admit is
        // not running late, it is misconfigured. "Missed its scheduled run" sends the user looking
        // for a service fault that is not there, and the job will still be there tomorrow.
        foreach (var job in jobs.Where(j => j.NeverRuns))
        {
            items.Add(new(AttentionSeverity.Warning,
                $"Job “{job.Name}” never runs — its maintenance window can never admit its schedule",
                "Open", job.Id));
        }

        foreach (var job in jobs.Where(j => j.IsScheduleOverdue(now) && !j.NeverRuns))
        {
            items.Add(new(AttentionSeverity.Warning, $"Job “{job.Name}” missed its scheduled run", "Open", job.Id));
        }

        foreach (var job in jobs.Where(j => skippedOccurrences.ContainsKey(j.Id)))
        {
            items.Add(new(AttentionSeverity.Warning,
                $"Job “{job.Name}” skipped a scheduled run — {FirstLine(skippedOccurrences[job.Id])}",
                "Open", job.Id));
        }

        foreach (var server in servers.Where(s => s.LastTestStatus == ConnectionTestStatus.Failed))
        {
            items.Add(new(AttentionSeverity.Error,
                $"Server “{server.Name}” failed its last connection test", "Open Servers", null));
        }

        // Repositories had no equivalent loop at all, which is an odd asymmetry: a server that
        // failed its last test raised a row, while a repository whose stored status was Failed —
        // an expired token, a repository that no longer resolves — raised nothing anywhere.
        // Judged on the EFFECTIVE status, the same value the Repositories page renders. Reading the
        // raw stored status made the two screens disagree: a Failed result older than the decay
        // window showed a red "failed its last check" row here while the page showed a neutral
        // "Not validated" pill, and the repository also collected a second row from the staleness
        // loop below for the same underlying fact.
        foreach (var repository in repositories.Where(
            r => r.EffectiveValidationStatus(now) == RepositoryValidationStatus.Failed))
        {
            items.Add(new(AttentionSeverity.Error,
                $"Repository “{repository.Name}” failed its last check — {FirstLine(repository.LastValidationDetail ?? "check it in Repositories")}",
                "Open Repositories", null, "Repositories"));
        }

        // A read-only token raised nothing, which is the wrong way round: it is the failure this
        // checker was built to catch. Every push-based job on it fails, and the page shows it as
        // Attention while the dashboard said nothing at all.
        foreach (var repository in repositories.Where(
            r => r.EffectiveValidationStatus(now) == RepositoryValidationStatus.Attention))
        {
            items.Add(new(AttentionSeverity.Warning,
                $"Repository “{repository.Name}” needs attention — {FirstLine(repository.LastValidationDetail ?? "check it in Repositories")}",
                "Open Repositories", null, "Repositories"));
        }

        // ...and a check nobody has re-run in a month is not evidence of health. The green pill was
        // permanent, so a token validated in January and expired in March still read as Valid in
        // September; only the hover tooltip carried the age.
        foreach (var repository in repositories.Where(r => r.IsValidationStale(now)))
        {
            items.Add(new(AttentionSeverity.Warning,
                $"Repository “{repository.Name}” has not been checked since "
                + (repository.LastValidatedAt is { } at ? at.LocalDateTime.ToString("d MMM yyyy") : "it was added"),
                "Open Repositories", null, "Repositories"));
        }

        // Last, and Error rather than Warning: runs are still completing normally, so nothing else
        // on this dashboard looks wrong — which is exactly why a silent alerting outage needs to be
        // stated. Naming the account is usually the whole diagnosis, because the common cause is
        // the service running under an account whose credential vault has no SMTP password.
        if (alertFailure is { } alert)
        {
            items.Add(new(AttentionSeverity.Error,
                $"{alert.Channel} alerts are not being delivered (as {alert.Account}) — {FirstLine(alert.Error)}",
                "Open Settings", null, "Settings"));
        }

        return items;
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return (end < 0 ? message : message[..end]).Trim();
    }
}
