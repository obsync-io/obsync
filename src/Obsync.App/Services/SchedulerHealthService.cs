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
    /// account (whose per-user database and credential vault are separate), or it is unresponsive.
    /// </summary>
    NotExecutingYourJobs,
}

/// <summary>The scheduler verdict plus the user-facing explanation.</summary>
public sealed record SchedulerHealth(SchedulerHealthStatus Status, string Summary)
{
    /// <summary>True when enabled schedules will actually fire.</summary>
    public bool CanExecuteSchedules => Status == SchedulerHealthStatus.Healthy;
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

    /// <summary>The service heartbeats every 30s; anything older than this is treated as dead.</summary>
    private static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(90);

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
        return Evaluate(QueryServiceStatus(), QueryServiceAccount(), heartbeat, _clock.UtcNow);
    }

    /// <summary>Pure verdict logic, separated so it is testable without a real service or registry.</summary>
    public static SchedulerHealth Evaluate(
        ServiceControllerStatus? serviceStatus, string? serviceAccount, SchedulerHeartbeat? heartbeat, DateTimeOffset nowUtc)
    {
        if (serviceStatus is null)
        {
            return new SchedulerHealth(
                SchedulerHealthStatus.NotInstalled,
                "Scheduled jobs won't run — the Obsync background service is not installed. " +
                "Reinstall Obsync and configure the service account to enable schedules.");
        }

        if (serviceStatus != ServiceControllerStatus.Running)
        {
            return new SchedulerHealth(
                SchedulerHealthStatus.NotRunning,
                "Scheduled jobs won't run — the Obsync background service is stopped. " +
                "Start the \"Obsync\" service (services.msc), or reinstall Obsync.");
        }

        var fresh = heartbeat is not null && nowUtc - heartbeat.TimestampUtc <= HeartbeatFreshness;
        if (fresh)
        {
            return new SchedulerHealth(
                SchedulerHealthStatus.Healthy,
                $"Scheduling active — the Obsync service is running as {heartbeat!.Account}.");
        }

        // Running per SCM, but no fresh heartbeat in THIS database. The dominant cause is the
        // service running under a different account (jobs, credentials, and the database itself are
        // per-user), which includes the LocalSystem default of a click-through install.
        var account = string.IsNullOrWhiteSpace(serviceAccount) ? "another account" : serviceAccount;
        return new SchedulerHealth(
            SchedulerHealthStatus.NotExecutingYourJobs,
            $"Scheduled jobs won't run — the Obsync service is running as {account}, which cannot see " +
            "your jobs or credentials. Set the service's Log On account to your Windows account " +
            "(services.msc → Obsync → Log On) and restart it, or reinstall Obsync with your account.");
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
