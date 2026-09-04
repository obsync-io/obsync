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

    /// <summary>The signed-in user, in the same shape CurrentActor.Name produces.</summary>
    private const string Me = "CORP\\dba";

    private static SchedulerHeartbeat Heartbeat(TimeSpan age) => new()
    {
        TimestampUtc = Now - age,
        Account = "CORP\\dba",
        Version = "1.0.0",
    };

    [Fact]
    public void NotInstalled_WhenTheServiceDoesNotExist()
    {
        var health = SchedulerHealthService.Evaluate(null, null, null, Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotInstalled, health.Status);
        Assert.False(health.CanExecuteSchedules);
    }

    [Fact]
    public void NotRunning_WhenTheServiceIsStopped()
    {
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Stopped, "CORP\\dba", Heartbeat(TimeSpan.FromSeconds(10)), Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotRunning, health.Status);
        Assert.False(health.CanExecuteSchedules);
    }

    [Fact]
    public void Healthy_WhenRunningWithAFreshHeartbeat()
    {
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\dba", Heartbeat(TimeSpan.FromSeconds(45)), Now, Me);

        Assert.Equal(SchedulerHealthStatus.Healthy, health.Status);
        Assert.True(health.CanExecuteSchedules);
        Assert.Contains("CORP\\dba", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NotExecutingYourJobs_WhenRunningWithoutAHeartbeat_NamesTheServiceAccount()
    {
        // The classic broken default: service running as LocalSystem, whose per-user database
        // (where the heartbeat would land) is not this user's.
        var health = SchedulerHealthService.Evaluate(ServiceControllerStatus.Running, "LocalSystem", null, Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotExecutingYourJobs, health.Status);
        Assert.False(health.CanExecuteSchedules);
        Assert.Contains("LocalSystem", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresponsive_WhenTheHeartbeatIsStale_AndNeverBlamesTheLogonAccount()
    {
        // A stale heartbeat proves the service DID write into this database, so its account can see
        // this user's jobs. Telling the user to set the Log On account to their own account would
        // be telling them to set it to what it already is.
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\dba", Heartbeat(TimeSpan.FromMinutes(10)), Now, Me);

        Assert.Equal(SchedulerHealthStatus.Unresponsive, health.Status);
        Assert.False(health.CanExecuteSchedules);
        Assert.DoesNotContain("Log On", health.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot see", health.Summary, StringComparison.Ordinal);
        Assert.Contains("10 minutes", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresponsive_NamesTheHeartbeatAccount_NotTheRegistryObjectName()
    {
        // The registry ObjectName and the heartbeat account use different shapes for the same
        // identity (".\alice" vs "MACHINE\alice"), so the stale message must quote the heartbeat —
        // the authoritative record of who last wrote into THIS database.
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, ".\\dba", Heartbeat(TimeSpan.FromMinutes(10)), Now, Me);

        Assert.Contains("CORP\\dba", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresponsive_WhenTheHeartbeatIsDatedInTheFuture()
    {
        // Unbounded freshness would treat a negative age as fresh and report a dead scheduler as
        // healthy until the wall clock caught up (fast RTC corrected by NTP, restored snapshot).
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\dba", Heartbeat(TimeSpan.FromHours(-2)), Now, Me);

        Assert.Equal(SchedulerHealthStatus.Unresponsive, health.Status);
        Assert.False(health.CanExecuteSchedules);
        Assert.Contains("system clock", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NotRunning_WhenStarting_DoesNotTellTheUserToStartIt()
    {
        // Delayed-auto-start means the app is routinely open while the service is still coming up.
        var health = SchedulerHealthService.Evaluate(ServiceControllerStatus.StartPending, "CORP\\dba", null, Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotRunning, health.Status);
        Assert.False(health.CanExecuteSchedules);
        Assert.Contains("still starting", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NotRunning_PointsAtTheLogonAccount_TheCauseAWaitlessInstallLeavesBehind()
    {
        // The MSI starts the service with Wait="no", so a blank/wrong password or a missing
        // "Log on as a service" right leaves a successfully installed but stopped service.
        var health = SchedulerHealthService.Evaluate(ServiceControllerStatus.Stopped, "CORP\\dba", null, Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotRunning, health.Status);
        Assert.Contains("Log On", health.Summary, StringComparison.Ordinal);
        Assert.Contains("Log on as a service", health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningAsAnotherAccount_WhenAFreshHeartbeatIsNotThisUsers()
    {
        // Only reachable with a shared OBSYNC_DATA_ROOT: the database is common, so the service
        // heartbeats here and schedules do fire — but Credential Manager stays per-user, so a
        // plain "Scheduling active" would suppress every warning while every run fails to auth.
        var heartbeat = new SchedulerHeartbeat
        {
            TimestampUtc = Now - TimeSpan.FromSeconds(10),
            Account = "CORP\\svc_obsync",
            Version = "1.0.0",
        };

        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\svc_obsync", heartbeat, Now, Me);

        Assert.Equal(SchedulerHealthStatus.RunningAsAnotherAccount, health.Status);
        Assert.True(health.CanExecuteSchedules); // the schedules genuinely fire — no dead-schedule banner
        Assert.Contains("CORP\\svc_obsync", health.Summary, StringComparison.Ordinal);
        Assert.Contains(Me, health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Healthy_WhenTheHeartbeatAccountDiffersOnlyByCase()
    {
        var heartbeat = new SchedulerHeartbeat
        {
            TimestampUtc = Now - TimeSpan.FromSeconds(10),
            Account = "corp\\DBA",
            Version = "1.0.0",
        };

        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Running, "CORP\\dba", heartbeat, Now, Me);

        Assert.Equal(SchedulerHealthStatus.Healthy, health.Status);
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

    [Fact]
    public void AFreshHeartbeat_OutranksTheSCMSayingTheServiceIsNotInstalled()
    {
        // "Reinstall Obsync" is the most destructive advice this surface can give, and it was being
        // produced while something was demonstrably scheduling into this database — reachable when
        // the scheduler lives on another host behind a shared data root, or when the SCM cannot be
        // read at all. A fresh beacon is positive proof and must win.
        var health = SchedulerHealthService.Evaluate(null, null, Heartbeat(TimeSpan.FromSeconds(10)), Now, Me);

        Assert.Equal(SchedulerHealthStatus.Healthy, health.Status);
        Assert.True(health.CanExecuteSchedules);
    }

    [Fact]
    public void AFreshHeartbeat_DoesNotOutrankAServiceTheSCMReportsAsStopped()
    {
        // SCM is authoritative for a service it can see: a beacon under 90s old is just residue
        // from a service killed moments ago, so this must stay NotRunning.
        var health = SchedulerHealthService.Evaluate(
            ServiceControllerStatus.Stopped, Me, Heartbeat(TimeSpan.FromSeconds(10)), Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotRunning, health.Status);
        Assert.False(health.CanExecuteSchedules);
    }

    [Fact]
    public void NotInstalled_StillWins_WhenNothingHasEverHeartbeated()
    {
        var health = SchedulerHealthService.Evaluate(null, null, null, Now, Me);

        Assert.Equal(SchedulerHealthStatus.NotInstalled, health.Status);
    }
}
