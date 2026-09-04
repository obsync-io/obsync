using System.ServiceProcess;
using Microsoft.Win32;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>Whether the scheduling engine can actually execute this user's schedules.</summary>
public enum SchedulerHealthStatus
{
    /// <summary>The service is running and heartbeating into this database — schedules will execute.</summary>
    Healthy,

    /// <summary>The Obsync Windows Service is not installed on this machine.</summary>
    NotInstalled,

    /// <summary>The service is installed but not running.</summary>
    NotRunning,

    /// <summary>
    /// The service is running but is not executing THIS user's jobs — it runs under a different
    /// account, whose per-user database and credential vault are separate.
    /// </summary>
    NotExecutingYourJobs,

    /// <summary>
    /// The service runs under an account that CAN see this database — it has heartbeated here
    /// before — but its heartbeat has gone stale. A liveness problem (busy, stuck, or killed),
    /// not an identity one, so the remedy is never "change the logon account".
    /// </summary>
    Unresponsive,

    /// <summary>
    /// The service is alive and scheduling against THIS database, but under a different account
    /// than the signed-in user — only reachable with a shared <c>OBSYNC_DATA_ROOT</c>, which makes
    /// the database common while Credential Manager stays per-user. Schedules fire, so this is not
    /// a scheduling failure; it is a warning that credentials saved here go to the wrong vault.
    /// </summary>
    RunningAsAnotherAccount,
}

/// <summary>The scheduler verdict plus the user-facing explanation.</summary>
public sealed record SchedulerHealth(SchedulerHealthStatus Status, string Summary)
{
    /// <summary>True when enabled schedules will actually fire. A service running under another
    /// account still fires this database's schedules, so it counts — the credential-vault caveat
    /// that comes with it is a diagnostics warning, not a "your schedules are dead" banner.</summary>
    public bool CanExecuteSchedules =>
        Status is SchedulerHealthStatus.Healthy or SchedulerHealthStatus.RunningAsAnotherAccount;
}

/// <summary>
/// Answers "will my scheduled jobs run?" by combining the Service Control Manager state, the
/// service's configured logon account, and the scheduler heartbeat the service writes into the
/// shared database. The heartbeat is the authoritative liveness signal: SCM can only say a process
/// is running, not that it is scheduling against <em>this user's</em> database.
/// </summary>
public interface ISchedulerHealthService
{
    Task<SchedulerHealth> GetAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISchedulerHealthService" />
public sealed class SchedulerHealthService : ISchedulerHealthService
{
    /// <summary>
    /// Whether this job depends on the background service at all — an enabled job with any
    /// non-manual cadence or a run-on-startup flag. Manual-only jobs run fine from the app.
    /// </summary>
    public static bool NeedsScheduler(SyncJob job) =>
        job.Enabled && (job.Schedule.Kind != ScheduleKind.Manual || job.Schedule.RunOnStartup);

    private const string ServiceName = "Obsync";

    /// <summary>The service heartbeats every 30s; anything older than this is treated as dead.
    /// Shared with <see cref="SupportInfoService"/> so "is the service running" has one definition.</summary>
    internal static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(90);

    private readonly IAppSettingsRepository _settings;
    private readonly IClock _clock;

    public SchedulerHealthService(IAppSettingsRepository settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
    }

    public async Task<SchedulerHealth> GetAsync(CancellationToken cancellationToken = default)
    {
        var heartbeat = await _settings.GetSchedulerHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        return Evaluate(QueryServiceStatus(), QueryServiceAccount(), heartbeat, _clock.UtcNow, CurrentActor.Name);
    }

    /// <summary>Pure verdict logic, separated so it is testable without a real service or registry.</summary>
    public static SchedulerHealth Evaluate(
        ServiceControllerStatus? serviceStatus,
        string? serviceAccount,
        SchedulerHeartbeat? heartbeat,
        DateTimeOffset nowUtc,
        string currentActor)
    {
        if (serviceStatus is null)
        {
            return new SchedulerHealth(
                SchedulerHealthStatus.NotInstalled,
                "Scheduled jobs won't run — the Obsync background service is not installed. " +
                "Reinstall Obsync and configure the service account to enable schedules.");
        }

        // Mid-transition, not broken: the service is registered delayed-auto-start, so the app is
        // routinely open while it is still coming up. Telling the user to start it would be wrong.
        if (serviceStatus is ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending)
        {
            return new SchedulerHealth(
                SchedulerHealthStatus.NotRunning,
                "The Obsync background service is still starting — scheduled jobs will run once it " +
                "has finished starting.");
        }

        if (serviceStatus != ServiceControllerStatus.Running)
        {
            // Both causes that survive a *successful* install are logon problems, because the MSI
            // starts the service with Wait="no" so a failed start never fails the install: a blank
            // or wrong password, and an account without the "Log on as a service" right. Naming the
            // Log On tab covers both — re-entering credentials there also grants the right.
            return new SchedulerHealth(
                SchedulerHealthStatus.NotRunning,
                "Scheduled jobs won't run — the Obsync background service is stopped. Start the " +
                "\"Obsync\" service (services.msc). If it will not stay started, its logon account is " +
                "the usual cause: on the service's Log On tab re-enter the account and password — " +
                "which also grants the \"Log on as a service\" right — then start it.");
        }

        // A heartbeat dated in the FUTURE is never fresh. An unbounded comparison reports a dead
        // scheduler as healthy until the wall clock catches up (a fast RTC corrected by NTP, or a
        // restored VM snapshot).
        var age = heartbeat is null ? (TimeSpan?)null : nowUtc - heartbeat.TimestampUtc;
        if (age is { } fresh && fresh >= TimeSpan.Zero && fresh <= HeartbeatFreshness)
        {
            // Freshness alone does NOT prove the service can do this user's work. The heartbeat
            // lands in this database, which is normally per-user — but a machine-wide
            // OBSYNC_DATA_ROOT makes it shared, and then a service under any account heartbeats
            // here. Windows Credential Manager is per-user regardless, so schedules would fire and
            // every run would fail to authenticate, with every warning suppressed. Both strings are
            // built from the same Environment.UserDomainName\UserName expression
            // (SchedulerBeacon.WriteAsync and CurrentActor.Name), so this compares like with like.
            if (!string.Equals(heartbeat!.Account, currentActor, StringComparison.OrdinalIgnoreCase))
            {
                return new SchedulerHealth(
                    SchedulerHealthStatus.RunningAsAnotherAccount,
                    $"Scheduling active, but the Obsync service is running as {heartbeat.Account} while you " +
                    $"are signed in as {currentActor}. It shares these jobs, but SQL passwords and GitHub " +
                    $"tokens are stored per-account — save them while signed in as {heartbeat.Account}, or " +
                    "scheduled runs will fail to authenticate.");
            }

            return new SchedulerHealth(
                SchedulerHealthStatus.Healthy,
                $"Scheduling active — the Obsync service is running as {heartbeat.Account}.");
        }

        if (heartbeat is null)
        {
            // Nothing has EVER heartbeated into this database, so the service is scheduling against
            // a different one: it runs under another account, whose data root and credential vault
            // are separate. This includes the LocalSystem default of a click-through install.
            var account = string.IsNullOrWhiteSpace(serviceAccount) ? "another account" : serviceAccount;
            return new SchedulerHealth(
                SchedulerHealthStatus.NotExecutingYourJobs,
                $"Scheduled jobs won't run — the Obsync service is running as {account}, which cannot see " +
                "your jobs or credentials. Set the service's Log On account to your Windows account " +
                "(services.msc → Obsync → Log On) and restart it, or reinstall Obsync with your account.");
        }

        // Stale, not absent. The service DID write into this database, so its account can see this
        // user's jobs and the account is NOT the problem — advising a logon change here would tell
        // the user to set the account to what it already is. This is a liveness question, and the
        // Quartz triggers may well still be firing, so the wording does not promise a failure.
        return new SchedulerHealth(
            SchedulerHealthStatus.Unresponsive,
            $"The Obsync service is running as {heartbeat.Account} but has not reported in for " +
            $"{DescribeAge(age!.Value)}. Scheduled jobs may be delayed — the service may be busy with a " +
            "long run, or stuck. If this persists, restart the \"Obsync\" service (services.msc) and " +
            "check the service log.");
    }

    /// <summary>Heartbeat age for the unresponsive message; a future timestamp means a clock problem.</summary>
    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            return "an unknown time — its last report is dated in the future, so check the system clock";
        }

        if (age.TotalMinutes < 1)
        {
            return "under a minute";
        }

        if (age.TotalHours < 1)
        {
            var minutes = (int)age.TotalMinutes;
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
        }

        var hours = (int)age.TotalHours;
        return $"{hours} hour{(hours == 1 ? string.Empty : "s")}";
    }

    /// <summary>The SCM status, or null when the service is not installed.</summary>
    private static ServiceControllerStatus? QueryServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
    }

    /// <summary>The service's configured logon account (registry ObjectName), or null when unreadable.</summary>
    private static string? QueryServiceAccount()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key?.GetValue("ObjectName") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
