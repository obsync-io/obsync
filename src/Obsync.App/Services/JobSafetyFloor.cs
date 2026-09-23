using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// The rules every route that writes a job must clear, in one place.
///
/// The wizard has always had them; the routes added later did not, and each gap was found
/// separately — the importer could save an unschedulable cadence, then a starved maintenance window,
/// then a traversing path, then a branch name git reads as an option. They are the same four rules
/// every time, so they live here rather than being re-listed at each call site and drifting again.
///
/// The wizard keeps its own per-field checks: they are stricter and report against the step the user
/// is looking at. This is the floor beneath them, for the routes with no user to correct.
/// </summary>
internal static class JobSafetyFloor
{
    /// <summary>
    /// Largest accepted lock timeout. Above this the conversion to milliseconds saturates at
    /// <see cref="int.MaxValue"/> (see <see cref="SqlLockTimeout"/>), so the job would silently get
    /// a different timeout than it asked for. A day is already far beyond any useful value.
    /// </summary>
    private const int MaxLockTimeoutSeconds = 86_400;

    /// <summary>The first reason this job cannot be run safely, or null when it clears every rule.</summary>
    public static string? FirstProblem(SyncJob job, DateTimeOffset nowUtc)
    {
        if (job.Schedule.UnschedulableReason() is { } cadence)
        {
            return cadence;
        }

        if (ScheduleWindowGuard.ConflictReason(job.Schedule, nowUtc) is { } window)
        {
            return window;
        }

        // Export-only jobs have no branch; a git-mode job's reaches git as a bare positional.
        if (job.CommitMode != CommitMode.ExportOnly
            && !string.IsNullOrWhiteSpace(job.Branch)
            && !GitRefName.IsValidBranchName(job.Branch.Trim()))
        {
            return $"The branch name \"{job.Branch.Trim()}\" is not a valid git branch name.";
        }

        // The rules below existed only in the wizard, which meant importing a job configuration
        // bypassed every one of them. An imported job could be saved ENABLED and then simply never
        // work: a cron schedule with no expression shows no next run and never fires; an export job
        // with no path fails at run time; an explicit database list that is empty scripts nothing and
        // reports success. This class is the shared floor both routes pass through, so the checks
        // belong here rather than in one of them.
        if (job.Schedule.Kind == ScheduleKind.Cron && string.IsNullOrWhiteSpace(job.Schedule.CronExpression))
        {
            return "This job uses a cron schedule but has no cron expression, so it would never run.";
        }

        if (job.CommitMode == CommitMode.ExportOnly && string.IsNullOrWhiteSpace(job.ExportPath))
        {
            return "This job exports to a folder or archive but has no export path.";
        }

        if (job.DatabaseScope == DatabaseScope.SelectedDatabases && job.Databases.Count == 0)
        {
            return "This job lists specific databases to script but the list is empty, "
                + "so it would script nothing and report success.";
        }

        // A negative command timeout throws from the SqlCommand setter rather than surfacing as a
        // SQL error, so it escapes the per-object containment and fails the whole run. A negative
        // reference-data cap skips every listed table and pins the run at Warning permanently.
        if (job.Advanced.SqlCommandTimeoutSeconds < 0)
        {
            return "The query timeout cannot be negative.";
        }

        if (job.Advanced.SqlLockTimeoutSeconds < 0)
        {
            return "The lock timeout cannot be negative.";
        }

        if (job.Advanced.ReferenceDataMaxRows < 0)
        {
            return "The reference-data row cap cannot be negative.";
        }

        // Upper bounds, not just lower ones. The wizard clamps all four of these at both ends
        // (CreateJobViewModel.SaveAsync), but import assigns Advanced wholesale from the file and
        // only ever reached the "cannot be negative" rules above — so every ceiling the wizard
        // enforces was reachable through an imported job:
        //
        //   MaxParallelWorkers  — used raw as the consumer-task count, and as the SMO fan-out.
        //   SqlRetryCount /
        //   GitRetryCount       — the worst of the set. The retry helpers loop on the count with a
        //                         growing delay, so a huge value turns one transient SQL or network
        //                         blip into a retry storm that never fails and never ends, holding
        //                         the job's run lock indefinitely. It fails SILENTLY, unlike the
        //                         others, which is why it is checked here even though a plain
        //                         "nonsensical value" is unlikely to be typed.
        //   SqlLockTimeoutSeconds — above ~24.8 days the milliseconds conversion saturates
        //                         (SqlLockTimeout.ToMilliseconds); refusing it is clearer than
        //                         silently honouring something else.
        if (job.Advanced.MaxParallelWorkers is < 0 or > 64)
        {
            return "The maximum parallel workers must be between 0 (automatic) and 64.";
        }

        if (job.Advanced.SqlRetryCount is < 1 or > 10)
        {
            return "The SQL retry count must be between 1 and 10.";
        }

        if (job.Advanced.GitRetryCount is < 1 or > 10)
        {
            return "The Git retry count must be between 1 and 10.";
        }

        if (job.Advanced.SqlLockTimeoutSeconds > MaxLockTimeoutSeconds)
        {
            return $"The lock timeout cannot exceed {MaxLockTimeoutSeconds:N0} seconds.";
        }

        return job.UnsafePathReason();
    }
}
