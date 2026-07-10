using System.ServiceProcess;
using Obsync.App.Services;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Covers the scheduler-health verdicts the UI banners and diagnostics rely on: not installed,
/// stopped, running-and-heartbeating (healthy), and running-but-invisible (wrong account / stale
/// heartbeat), plus which jobs need the scheduler at all.
/// </summary>
public sealed class SchedulerHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    private static SchedulerHeartbeat Heartbeat(TimeSpan age) => new()
    {
        TimestampUtc = Now - age,
        Account = "CORP\\dba",
        Version = "1.0.0",
    };

    [Fact]
    public void NotInstalled_WhenTheServiceDoesNotExist()
    {
        var health = SchedulerHealthService.Evaluate(null, null, null, Now);

        Assert.Equal(SchedulerHealthStatus.NotInstalled, health.Status);
        Assert.False(health.CanExecuteSchedules);
    }

    [Fact]
    public void NotRunning_WhenTheServiceIsStopped()
    {
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Stopped, "CORP\\dba", Heartbeat(TimeSpan.FromSeconds(10)), Now);

        Assert.Equal(SchedulerHealthStatus.NotRunning, health.Status);
        Assert.False(health.CanExecuteSchedules);
    }

    [Fact]
    public void Healthy_WhenRunningWithAFreshHeartbeat()
    {
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\dba", Heartbeat(TimeSpan.FromSeconds(45)), Now);

        Assert.Equal(SchedulerHealthStatus.Healthy, health.Status);
        Assert.True(health.CanExecuteSchedules);
        Assert.Contains("CORP\\dba", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NotExecutingYourJobs_WhenRunningWithoutAHeartbeat_NamesTheServiceAccount()
    {
        // The classic broken default: service running as LocalSystem, whose per-user database
        // (where the heartbeat would land) is not this user's.
        var health = SchedulerHealthService.Evaluate(ServiceControllerStatus.Running, "LocalSystem", null, Now);

        Assert.Equal(SchedulerHealthStatus.NotExecutingYourJobs, health.Status);
        Assert.False(health.CanExecuteSchedules);
        Assert.Contains("LocalSystem", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NotExecutingYourJobs_WhenTheHeartbeatIsStale()
    {
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\dba", Heartbeat(TimeSpan.FromMinutes(10)), Now);

        Assert.Equal(SchedulerHealthStatus.NotExecutingYourJobs, health.Status);
        Assert.False(health.CanExecuteSchedules);
    }

    [Theory]
    [InlineData(ScheduleKind.Daily, false, true, true)]   // enabled schedule → needs the service
    [InlineData(ScheduleKind.Manual, true, true, true)]   // run-on-startup fires from the service too
    [InlineData(ScheduleKind.Manual, false, true, false)] // manual-only → app Run Now suffices
    [InlineData(ScheduleKind.Daily, false, false, false)] // disabled → nothing to execute
    public void NeedsScheduler_OnlyForEnabledJobsWithSomethingToFire(
        ScheduleKind kind, bool runOnStartup, bool enabled, bool expected)
    {
        var job = new SyncJob
        {
            Name = "j",
            Enabled = enabled,
            Schedule = new ScheduleProfile { Kind = kind, RunOnStartup = runOnStartup },
        };

        Assert.Equal(expected, SchedulerHealthService.NeedsScheduler(job));
    }
}
