using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
using Obsync.Scheduler;

namespace Obsync.Service;

/// <summary>
/// Periodically reconciles the live Quartz schedule with the database so changes made in the desktop
/// app — creating a job, editing a schedule, enabling/disabling, or deleting — take effect within one
/// interval instead of requiring a service restart. Each tick also refreshes the scheduler heartbeat,
/// which is how the app knows a scheduler is alive and executing this database's jobs.
/// </summary>
public sealed class JobReconciliationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ISyncJobScheduler _scheduler;
    private readonly IAppSettingsRepository _settings;
    private readonly ILogger<JobReconciliationService> _logger;

    public JobReconciliationService(
        ISyncJobScheduler scheduler, IAppSettingsRepository settings, ILogger<JobReconciliationService> logger)
    {
        _scheduler = scheduler;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // The beacon is written FIRST and in its own try, deliberately. It is a liveness signal,
            // and the Quartz triggers keep firing whether or not a reconcile succeeds — so coupling
            // the two made a transient reconcile failure (a SQLITE_BUSY behind the engine's
            // whole-batch state transaction, say) report a perfectly healthy scheduler to the app
            // as dead. Two consecutive skips already exceed SchedulerHealthService's 90s window.
            try
            {
                await SchedulerBeacon.WriteAsync(_settings, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh the scheduler heartbeat; will retry.");
            }

            try
            {
                await _scheduler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a transient reconcile failure kill the loop; try again next tick.
                _logger.LogError(ex, "Job schedule reconciliation failed; will retry.");
            }
        }
    }
}
