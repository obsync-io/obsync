using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Obsync.Service;

/// <summary>
/// Cancels in-flight sync runs on service stop. Quartz's WaitForJobsToComplete only waits — it
/// never signals cancellation — so without this a Stop-Service during a long run ends with the SCM
/// killing the process mid-run. <see cref="IScheduler.Interrupt(JobKey, CancellationToken)"/>
/// cancels each executing job's context token, which <c>SyncQuartzJob</c> passes to the engine,
/// and the engine persists a clean Cancelled run. Must be registered AFTER AddQuartzHostedService:
/// hosted services stop in reverse registration order, so this cancels (and briefly drains) the
/// runs before the Quartz host begins its own wait.
/// </summary>
public sealed class RunCancellationOnStopService : IHostedService
{
    /// <summary>How long to wait for interrupted runs to persist their Cancelled result.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<RunCancellationOnStopService> _logger;

    public RunCancellationOnStopService(ISchedulerFactory schedulerFactory, ILogger<RunCancellationOnStopService> logger)
    {
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
            var executing = await scheduler.GetCurrentlyExecutingJobs(cancellationToken).ConfigureAwait(false);
            if (executing.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Service stopping — cancelling {Count} in-flight run(s).", executing.Count);
            foreach (var context in executing)
            {
                await scheduler.Interrupt(context.JobDetail.Key, cancellationToken).ConfigureAwait(false);
            }

            var deadline = DateTime.UtcNow + DrainTimeout;
            while (DateTime.UtcNow < deadline
                && (await scheduler.GetCurrentlyExecutingJobs(cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a failure here just falls back to the pre-existing hard-stop behavior.
            _logger.LogWarning(ex, "Could not cancel in-flight runs on service stop.");
        }
    }
}
