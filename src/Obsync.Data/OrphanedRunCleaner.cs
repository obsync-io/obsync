using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Data;

/// <summary>
/// Crash recovery for run history: fails "Running" rows whose owning process died before finishing.
/// A live run — in ANY host — holds its job's <see cref="JobRunLock"/> for its entire duration
/// (acquired before the row is inserted, released after final persistence), so a Running row whose
/// lock is free is definitionally orphaned. Probing the lock instead of using an age cutoff means a
/// legitimate multi-hour VLDB run in the service is never falsely failed by the app starting up.
/// </summary>
public static class OrphanedRunCleaner
{
    /// <summary>Fails orphaned Running rows; returns the runs it failed (mirroring the persisted
    /// state) so the caller can alert on them — the process that ran them died before it could.</summary>
    public static async Task<IReadOnlyList<SyncRun>> CleanAsync(
        IRunRepository runs, string locksRoot, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        const string reason = "Run interrupted — the process running it exited before it finished.";

        var cleaned = new List<SyncRun>();
        foreach (var run in await runs.GetRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            if (JobRunLock.IsHeld(locksRoot, run.JobId))
            {
                continue; // genuinely in progress in some Obsync process
            }

            await runs.FailRunAsync(run.Id, nowUtc, reason, cancellationToken).ConfigureAwait(false);
            run.Status = RunStatus.Failed;
            run.CompletedAt = nowUtc;
            run.ErrorMessage = reason;
            cleaned.Add(run);
        }

        return cleaned;
    }
}
