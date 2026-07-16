using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Shared.Tests;

/// <summary>
/// The overdue rule behind the "Overdue" indicators: an enabled, scheduled, not-running job whose
/// cached next-run time passed more than the grace period ago. Everything else — disabled, manual,
/// never scheduled, still in the future, inside the grace window, or currently running — is not
/// overdue.
/// </summary>
public sealed class SyncJobOverdueTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static SyncJob NewJob(
        bool enabled = true,
        ScheduleKind kind = ScheduleKind.Daily,
        DateTimeOffset? nextRunAt = null,
        bool isRunning = false) => new()
    {
        Name = "Job",
        Enabled = enabled,
        Schedule = new ScheduleProfile { Kind = kind },
        RunSummary = new JobRunSummary { NextRunAt = nextRunAt },
        IsRunning = isRunning,
    };

    public static TheoryData<string, SyncJob, bool> Cases => new()
    {
        { "paused job", NewJob(enabled: false, nextRunAt: Now.AddHours(-1)), false },
        { "manual schedule", NewJob(kind: ScheduleKind.Manual, nextRunAt: Now.AddHours(-1)), false },
        { "no cached next run", NewJob(nextRunAt: null), false },
        { "next run in the future", NewJob(nextRunAt: Now.AddMinutes(30)), false },
        { "within the 5-minute grace", NewJob(nextRunAt: Now.AddMinutes(-4)), false },
        { "exactly at the grace boundary", NewJob(nextRunAt: Now - SyncJob.ScheduleOverdueGrace), false },
        { "past the grace period", NewJob(nextRunAt: Now.AddMinutes(-6)), true },
        { "cron schedules participate too", NewJob(kind: ScheduleKind.Cron, nextRunAt: Now.AddHours(-1)), true },
        { "currently running", NewJob(nextRunAt: Now.AddHours(-1), isRunning: true), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsScheduleOverdue_MatchesTheRule(string label, SyncJob job, bool expected) =>
        Assert.True(expected == job.IsScheduleOverdue(Now), label);
}
