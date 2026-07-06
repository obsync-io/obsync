using Obsync.Data.Repositories;

namespace Obsync.Data;

/// <summary>
/// Applies the configured run-history retention: deletes runs older than the setting (with their
/// logs and changes). Shared by the app (once at startup) and the service (daily). A retention of
/// 0 (the default) keeps everything.
/// </summary>
public static class RunRetention
{
    public static async Task<int> CleanupAsync(
        IAppSettingsRepository settings, IRunRepository runs, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var days = await settings.GetRunRetentionDaysAsync(cancellationToken).ConfigureAwait(false);
        if (days <= 0)
        {
            return 0;
        }

        return await runs.DeleteRunsBeforeAsync(now.AddDays(-days), cancellationToken).ConfigureAwait(false);
    }
}
