using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
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
    /// RunOnStartup job is fired immediately — true at startup / first schedule, false when merely
    /// applying a changed schedule so an edit does not kick off an unexpected run.
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
        var jobs = await _jobs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs.Where(j => j.Enabled))
        {
            await ScheduleJobAsync(job, triggerStartupRun: true, cancellationToken).ConfigureAwait(false);
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
            await scheduler.TriggerJob(jobKey, cancellationToken).ConfigureAwait(false);
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
            await scheduler.DeleteJob(key, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Unscheduled job {Key} (deleted or disabled).", key.Name);
        }

        // Add new jobs, apply changed cadences, and keep the cached next-run fresh.
        foreach (var job in enabled)
        {
            var jobKey = new JobKey(job.Id.ToString("N"), Group);
            var triggerKey = new TriggerKey(job.Id.ToString("N"), Group);
            var cron = CronTranslator.ToCron(job.Schedule);
            var hasCron = !string.IsNullOrWhiteSpace(cron) && CronExpression.IsValidExpression(cron);

            if (!await scheduler.CheckExists(jobKey, cancellationToken).ConfigureAwait(false))
            {
                await ScheduleJobAsync(job, triggerStartupRun: true, cancellationToken).ConfigureAwait(false);
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
    }
}
