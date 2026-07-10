using Obsync.Shared.Models;

namespace Obsync.Shared;

/// <summary>
/// The missed-run policy: when the scheduler starts, a job whose persisted next-run time passed
/// while the scheduler was offline (machine off, service stopped, or a delayed service start) is
/// run ONCE as a catch-up — never once per missed occurrence. Kept as pure logic so it is
/// exhaustively testable without Quartz.
/// </summary>
public static class MissedRunPolicy
{
    /// <summary>
    /// Whether a catch-up run should fire for this job at scheduler startup, given the next-run time
    /// that was persisted before the scheduler went down.
    /// </summary>
    /// <remarks>
    /// No catch-up when:
    /// there was no persisted fire time (new job, manual schedule, or already cleared);
    /// the fire time is still in the future (nothing was missed);
    /// a run already covered it (any run started at/after the missed time — scheduled, manual, or CLI —
    /// counts, so recovery never duplicates work);
    /// the job runs on startup anyway (the startup run IS the catch-up).
    /// </remarks>
    public static bool ShouldCatchUp(SyncJob job, DateTimeOffset nowUtc)
    {
        if (!job.Enabled || job.Schedule.RunOnStartup)
        {
            return false;
        }

        if (job.RunSummary.NextRunAt is not { } missedFire || missedFire > nowUtc)
        {
            return false;
        }

        return job.RunSummary.LastRunAt is not { } lastRun || lastRun < missedFire;
    }
}
