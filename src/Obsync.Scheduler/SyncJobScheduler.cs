using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;
using Quartz.Impl.Matchers;

namespace Obsync.Scheduler;

/// <summary>Schedules sync jobs with Quartz based on their <see cref="ScheduleProfile"/>.</summary>
public interface ISyncJobScheduler
{
    /// <summary>Schedules every enabled job (used once at service startup; honors RunOnStartup).</summary>
    Task ScheduleAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules (or reschedules) a single job. <paramref name="triggerStartupRun"/> controls whether a
    /// RunOnStartup job is fired immediately — true only at service startup (ScheduleAllAsync), false
    /// when applying a schedule while the service is running (reconcile, edits) so a job becoming
    /// newly scheduled does not kick off an unattended run.
    /// </summary>
    Task ScheduleJobAsync(SyncJob job, bool triggerStartupRun = true, CancellationToken cancellationToken = default);

    Task UnscheduleJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the live Quartz schedule in line with the database: schedules new jobs, reschedules jobs
    /// whose cadence changed, and unschedules jobs that were deleted or disabled. Called periodically so
    /// changes made in the app take effect without restarting the service.
    /// </summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISyncJobScheduler" />
public sealed class SyncJobScheduler : ISyncJobScheduler
{
    private const string Group = "obsync";

    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IJobRepository _jobs;
    private readonly ILogger<SyncJobScheduler> _logger;

    public SyncJobScheduler(ISchedulerFactory schedulerFactory, IJobRepository jobs, ILogger<SyncJobScheduler> logger)
    {
        _schedulerFactory = schedulerFactory;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task ScheduleAllAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var jobs = await _jobs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs.Where(j => j.Enabled))
        {
            // Per-job fault isolation: one job Quartz rejects must not fail service startup for the rest.
            try
            {
                // Evaluate BEFORE scheduling: ScheduleJobAsync overwrites the persisted next-run (the
                // evidence of a fire time missed while the scheduler was down) with the next future fire.
                var catchUp = MissedRunPolicy.ShouldCatchUp(job, nowUtc) && CronTranslator.IsValid(job.Schedule);

                await ScheduleJobAsync(job, triggerStartupRun: true, cancellationToken).ConfigureAwait(false);

                if (catchUp)
                {
                    var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Job {Name}: a scheduled run (due {Due:u}) was missed while the scheduler was offline; running once now.",
                        job.Name, job.RunSummary.NextRunAt);
                    await scheduler.TriggerJob(
                        new JobKey(job.Id.ToString("N"), Group),
                        new JobDataMap { [SyncQuartzJob.TriggerKey] = nameof(RunTrigger.CatchUp) },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Job {Name}: could not be scheduled; continuing with the remaining jobs.", job.Name);
            }
        }
    }

    public async Task ScheduleJobAsync(SyncJob job, bool triggerStartupRun = true, CancellationToken cancellationToken = default)
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var jobKey = new JobKey(job.Id.ToString("N"), Group);

        if (await scheduler.CheckExists(jobKey, cancellationToken).ConfigureAwait(false))
        {
            await scheduler.DeleteJob(jobKey, cancellationToken).ConfigureAwait(false);
        }

        var cron = CronTranslator.ToCron(job.Schedule);
        var hasCron = !string.IsNullOrWhiteSpace(cron) && CronExpression.IsValidExpression(cron);

        // A syntactically valid cron can still never fire (e.g. a past year, Feb 31). Quartz's
        // ScheduleJob throws for such a trigger, so degrade to unscheduled instead.
        if (hasCron && CronTranslator.NextFire(cron!, DateTimeOffset.UtcNow) is null)
        {
            _logger.LogError("Job {Name}: cron '{Cron}' never fires; leaving it unscheduled.", job.Name, cron);
            hasCron = false;
        }

        if (!hasCron && !job.Schedule.RunOnStartup)
        {
            await _jobs.UpdateNextRunAtAsync(job.Id, null, cancellationToken).ConfigureAwait(false);
            return; // manual-only, nothing to schedule
        }

        var detail = JobBuilder.Create<SyncQuartzJob>()
            .WithIdentity(jobKey)
            .UsingJobData(SyncQuartzJob.JobIdKey, job.Id.ToString())
            .StoreDurably()
            .Build();

        if (hasCron)
        {
            var trigger = TriggerBuilder.Create()
                .WithIdentity(job.Id.ToString("N"), Group)
                .ForJob(jobKey)
                .WithCronSchedule(cron!, x => x.InTimeZone(TimeZoneInfo.Local))
                .Build();
            await scheduler.ScheduleJob(detail, trigger, cancellationToken).ConfigureAwait(false);

            var scheduled = await scheduler.GetTrigger(new TriggerKey(job.Id.ToString("N"), Group), cancellationToken).ConfigureAwait(false);
            await _jobs.UpdateNextRunAtAsync(job.Id, scheduled?.GetNextFireTimeUtc(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Scheduled job {Name} ({Cron}).", job.Name, cron);
        }
        else
        {
            await scheduler.AddJob(detail, replace: true, cancellationToken).ConfigureAwait(false);
            await _jobs.UpdateNextRunAtAsync(job.Id, null, cancellationToken).ConfigureAwait(false);
        }

        if (job.Schedule.RunOnStartup && triggerStartupRun)
        {
            // Attributed as a Startup run (not Scheduled) so History reads honestly and the
            // maintenance window does not apply, matching the documented bypass.
            await scheduler.TriggerJob(
                jobKey,
                new JobDataMap { [SyncQuartzJob.TriggerKey] = nameof(RunTrigger.Startup) },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UnscheduleJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        await scheduler.DeleteJob(new JobKey(jobId.ToString("N"), Group), cancellationToken).ConfigureAwait(false);
        await _jobs.UpdateNextRunAtAsync(jobId, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var jobs = await _jobs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var enabled = jobs.Where(j => j.Enabled).ToList();
        var desiredKeys = enabled.Select(j => new JobKey(j.Id.ToString("N"), Group)).ToHashSet();

        // Remove anything scheduled that is no longer wanted (deleted or disabled in the app).
        var existingKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(Group), cancellationToken).ConfigureAwait(false);
        foreach (var key in existingKeys.Where(k => !desiredKeys.Contains(k)))
        {
            // Per-job fault isolation: one failing job must not stop this tick (or the heartbeat).
            try
            {
                await scheduler.DeleteJob(key, cancellationToken).ConfigureAwait(false);
                if (Guid.TryParseExact(key.Name, "N", out var jobId))
                {
                    // Clear the cached next-run so a disabled job stops advertising a fire time that
                    // will never happen (a no-op for deleted jobs — the row is gone).
                    await _jobs.UpdateNextRunAtAsync(jobId, null, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Unscheduled job {Key} (deleted or disabled).", key.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Job {Key}: could not be unscheduled; continuing with the remaining jobs.", key.Name);
            }
        }

        // Add new jobs, apply changed cadences, and keep the cached next-run fresh.
        foreach (var job in enabled)
        {
            try
            {
                var jobKey = new JobKey(job.Id.ToString("N"), Group);
                var triggerKey = new TriggerKey(job.Id.ToString("N"), Group);
                var cron = CronTranslator.ToCron(job.Schedule);
                var hasCron = !string.IsNullOrWhiteSpace(cron) && CronExpression.IsValidExpression(cron);

                if (!await scheduler.CheckExists(jobKey, cancellationToken).ConfigureAwait(false))
                {
                    // No startup run here: a job that becomes newly scheduled while the service runs
                    // (re-enabled, or RunOnStartup switched on) must wait for its trigger — startup
                    // runs belong to ScheduleAllAsync (service start) only.
                    await ScheduleJobAsync(job, triggerStartupRun: false, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (hasCron)
                {
                    var trigger = await scheduler.GetTrigger(triggerKey, cancellationToken).ConfigureAwait(false) as ICronTrigger;
                    if (trigger?.CronExpressionString != cron)
                    {
                        // Cadence changed in the app → reschedule, but do NOT re-fire the startup run.
                        await ScheduleJobAsync(job, triggerStartupRun: false, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _jobs.UpdateNextRunAtAsync(job.Id, trigger?.GetNextFireTimeUtc(), cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (!job.Schedule.RunOnStartup)
                {
                    // Switched to manual-only → drop the trigger.
                    await scheduler.DeleteJob(jobKey, cancellationToken).ConfigureAwait(false);
                    await _jobs.UpdateNextRunAtAsync(job.Id, null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Job {Name}: could not be reconciled; continuing with the remaining jobs.", job.Name);
            }
        }
    }
}
