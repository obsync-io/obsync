using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine.Alerting;
using Obsync.Scheduler;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Service;

/// <summary>
/// On startup, initializes the local database, recovers runs orphaned by a crash, and schedules all
/// enabled jobs (including a one-time catch-up for schedules missed while the service was down —
/// see <see cref="MissedRunPolicy"/>). On graceful shutdown, clears the scheduler heartbeat so the
/// app immediately knows scheduled execution is off.
/// </summary>
public sealed class JobSchedulingBootstrapper : IHostedService
{
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly ISyncJobScheduler _scheduler;
    private readonly IRunRepository _runs;
    private readonly IAppSettingsRepository _settings;
    private readonly IRunAlertService _alerts;
    private readonly ILogger<JobSchedulingBootstrapper> _logger;

    public JobSchedulingBootstrapper(
        IDatabaseInitializer databaseInitializer,
        ISyncJobScheduler scheduler,
        IRunRepository runs,
        IAppSettingsRepository settings,
        IRunAlertService alerts,
        ILogger<JobSchedulingBootstrapper> logger)
    {
        _databaseInitializer = databaseInitializer;
        _scheduler = scheduler;
        _runs = runs;
        _settings = settings;
        _alerts = alerts;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Beacon as soon as the database can take it. The SCM reports SERVICE_RUNNING once OnStart
        // returns, so every step below — crash recovery, one alert per recovered run (each with its
        // own multi-second timeout), then scheduling every job — is time the app would otherwise
        // spend telling a correctly configured user that the service runs under the wrong account.
        // It is refreshed again after ScheduleAllAsync, and every 30s by JobReconciliationService.
        await WriteBeaconAsync(cancellationToken).ConfigureAwait(false);

        // Crash recovery: fail "Running" rows whose owning process died (lock no longer held), so a
        // service or machine crash mid-run leaves an honest Failed entry instead of a stuck one.
        var recovered = await OrphanedRunCleaner.CleanAsync(
            _runs, ObsyncPaths.LocksRoot, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (recovered.Count > 0)
        {
            _logger.LogWarning("Recovered {Count} run(s) interrupted by an earlier crash.", recovered.Count);
        }

        // Crash-recovered failures still alert — the process that ran them died before it could.
        // Best-effort, like the engine's own post-run alerting.
        foreach (var run in recovered)
        {
            try
            {
                await _alerts.NotifyAsync(run, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send the failure alert for recovered run {RunKey}.", run.RunKey);
            }
        }

        await _scheduler.ScheduleAllAsync(cancellationToken).ConfigureAwait(false);
        await WriteBeaconAsync(cancellationToken).ConfigureAwait(false);

        // Log the identity so credential-isolation problems are diagnosable: secrets in Windows
        // Credential Manager are per-user, so scheduled runs only work if the app saved them under
        // this same account (see the SQL/GitHub credential checks in SyncEngine).
        _logger.LogInformation(
            "Obsync service started and jobs scheduled. Running as {Domain}\\{User}.",
            Environment.UserDomainName, Environment.UserName);
    }

    /// <summary>
    /// Best-effort beacon write. A liveness signal must never be able to abort service startup —
    /// the app and the service race to migrate the same database on install (the MSI starts the
    /// service and launches the app back-to-back), so this write can legitimately hit SQLITE_BUSY.
    /// </summary>
    private async Task WriteBeaconAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SchedulerBeacon.WriteAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the scheduler heartbeat; the next tick will retry.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Clearing the beacon tells the app "scheduling is off" immediately instead of after
            // the staleness window. Best-effort: a failed clear just means the beacon goes stale.
            await _settings.SetSchedulerHeartbeatAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the scheduler heartbeat on shutdown.");
        }
    }
}

/// <summary>Writes the scheduler's liveness beacon (see <see cref="SchedulerHeartbeat"/>).</summary>
internal static class SchedulerBeacon
{
    public static Task WriteAsync(IAppSettingsRepository settings, CancellationToken cancellationToken) =>
        settings.SetSchedulerHeartbeatAsync(new SchedulerHeartbeat
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Account = $"{Environment.UserDomainName}\\{Environment.UserName}",
            Version = VersionInfo.Of(typeof(SchedulerBeacon).Assembly),
        }, cancellationToken);
}
