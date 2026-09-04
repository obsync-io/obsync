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

        return job.UnsafePathReason();
    }
}
