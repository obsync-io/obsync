using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Obsync.Data;
using Obsync.Data.Repositories;
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
    private readonly ILogger<JobSchedulingBootstrapper> _logger;

    public JobSchedulingBootstrapper(
        IDatabaseInitializer databaseInitializer,
        ISyncJobScheduler scheduler,
        IRunRepository runs,
        IAppSettingsRepository settings,
        ILogger<JobSchedulingBootstrapper> logger)
    {
        _databaseInitializer = databaseInitializer;
        _scheduler = scheduler;
        _runs = runs;
        _settings = settings;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Crash recovery: fail "Running" rows whose owning process died (lock no longer held), so a
        // service or machine crash mid-run leaves an honest Failed entry instead of a stuck one.
        var recovered = await OrphanedRunCleaner.CleanAsync(
            _runs, ObsyncPaths.LocksRoot, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            _logger.LogWarning("Recovered {Count} run(s) interrupted by an earlier crash.", recovered);
        }

        await _scheduler.ScheduleAllAsync(cancellationToken).ConfigureAwait(false);
        await SchedulerBeacon.WriteAsync(_settings, cancellationToken).ConfigureAwait(false);

        // Log the identity so credential-isolation problems are diagnosable: secrets in Windows
        // Credential Manager are per-user, so scheduled runs only work if the app saved them under
        // this same account (see the SQL/GitHub credential checks in SyncEngine).
        _logger.LogInformation(
            "Obsync service started and jobs scheduled. Running as {Domain}\\{User}.",
            Environment.UserDomainName, Environment.UserName);
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
