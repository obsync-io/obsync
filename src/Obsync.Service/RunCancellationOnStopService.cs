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
    /// <summary>How long to wait for interrupted runs to persist their Cancelled result. Kept well
    /// inside the host's ShutdownTimeout so Quartz's own WaitForJobsToComplete still has a budget
    /// after this returns — see ServiceShutdownBudget for how the whole budget is apportioned and
    /// why it has to fit inside the installer's 30-second cap.</summary>
    private static readonly TimeSpan DrainTimeout = ServiceShutdownBudget.Drain;

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

            // The drain runs on its OWN deadline, linked to the host's. Draining directly on the
            // host token let this consume the entire ShutdownTimeout and leave Quartz's
            // WaitForJobsToComplete nothing — and an exhausted host budget surfaces as an exception
            // out of Host.StopAsync, which Program.cs reports to the SCM as a fatal exit, turning a
            // deliberate Stop-Service into a "failure" that trips the MSI's 60s auto-restart.
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(DrainTimeout);

            try
            {
                while ((await scheduler.GetCurrentlyExecutingJobs(drain.Token).ConfigureAwait(false)).Count > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), drain.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Distinct from the catch below: the interrupts WERE issued, only the wait expired.
                // Logging that as "could not cancel" sent support after the wrong thing.
                _logger.LogWarning(
                    "Timed out after {Timeout}s waiting for in-flight runs to cancel; Quartz will wait for the rest.",
                    DrainTimeout.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a failure here just falls back to the pre-existing hard-stop behavior.
            _logger.LogWarning(ex, "Could not cancel in-flight runs on service stop.");
        }
    }
}
