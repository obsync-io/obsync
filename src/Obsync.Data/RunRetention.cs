using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;

namespace Obsync.Data;

/// <summary>
/// Applies the configured run-history retention: deletes runs older than the setting (with their
/// logs and changes). Shared by the app (once at startup) and the service (daily). A retention of
/// 0 (the default) keeps everything.
/// </summary>
public static class RunRetention
{
    public static async Task<int> CleanupAsync(
        IAppSettingsRepository settings, IRunRepository runs, IAuditWriter audit, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var days = await settings.GetRunRetentionDaysAsync(cancellationToken).ConfigureAwait(false);
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = now.AddDays(-days);
        var deleted = await runs.DeleteRunsBeforeAsync(cutoff, cancellationToken).ConfigureAwait(false);

        // Audited here rather than in the callers so both hosts are covered by one edit, and so the
        // actor is whichever identity actually did the pruning — the interactive user for the app's
        // startup pass, the service account for the daily one. Silent only when nothing was removed.
        if (deleted > 0)
        {
            await audit.WriteAsync(
                AuditAction.RunHistoryPruned,
                entityType: "RunHistory",
                entityId: null,
                entityName: null,
                detail: $"Deleted {deleted} run(s) started before {cutoff:u} (retention {days} days).",
                cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }
}
