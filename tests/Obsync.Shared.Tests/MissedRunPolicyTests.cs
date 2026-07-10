using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Shared.Tests;

public sealed class MissedRunPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    private static SyncJob Job(DateTimeOffset? nextRunAt, DateTimeOffset? lastRunAt = null,
        bool enabled = true, bool runOnStartup = false) => new()
    {
        Name = "j",
        Enabled = enabled,
        Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily, RunOnStartup = runOnStartup },
        RunSummary = new JobRunSummary { NextRunAt = nextRunAt, LastRunAt = lastRunAt },
    };

    [Fact]
    public void MissedFire_WithNoLaterRun_CatchesUp() =>
        Assert.True(MissedRunPolicy.ShouldCatchUp(Job(Now.AddHours(-3)), Now));

    [Fact]
    public void MissedFire_AlreadyCoveredByALaterRun_DoesNotCatchUp() =>
        // Any run at/after the missed time counts — scheduled, manual, or CLI — so recovery
        // never duplicates work.
        Assert.False(MissedRunPolicy.ShouldCatchUp(Job(Now.AddHours(-3), lastRunAt: Now.AddHours(-1)), Now));

    [Fact]
    public void MissedFire_WithAnOlderLastRun_StillCatchesUp() =>
        Assert.True(MissedRunPolicy.ShouldCatchUp(Job(Now.AddHours(-3), lastRunAt: Now.AddDays(-2)), Now));

    [Fact]
    public void FutureFire_DoesNotCatchUp() =>
        Assert.False(MissedRunPolicy.ShouldCatchUp(Job(Now.AddHours(2)), Now));

    [Fact]
    public void NoPersistedFireTime_DoesNotCatchUp() =>
        Assert.False(MissedRunPolicy.ShouldCatchUp(Job(null), Now));

    [Fact]
    public void RunOnStartupJob_DoesNotCatchUp_TheStartupRunCoversIt() =>
        Assert.False(MissedRunPolicy.ShouldCatchUp(Job(Now.AddHours(-3), runOnStartup: true), Now));

    [Fact]
    public void DisabledJob_DoesNotCatchUp() =>
        Assert.False(MissedRunPolicy.ShouldCatchUp(Job(Now.AddHours(-3), enabled: false), Now));
}
