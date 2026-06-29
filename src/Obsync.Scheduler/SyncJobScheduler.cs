using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.Scheduler;

/// <summary>Schedules sync jobs with Quartz based on their <see cref="ScheduleProfile"/>.</summary>
public interface ISyncJobScheduler
{
    Task ScheduleAllAsync(CancellationToken cancellationToken = default);
    Task ScheduleJobAsync(SyncJob job, CancellationToken cancellationToken = default);
    Task UnscheduleJobAsync(Guid jobId, CancellationToken cancellationToken = default);
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
            await ScheduleJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ScheduleJobAsync(SyncJob job, CancellationToken cancellationToken = default)
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
            _logger.LogInformation("Scheduled job {Name} ({Cron}).", job.Name, cron);
        }
        else
        {
            await scheduler.AddJob(detail, replace: true, cancellationToken).ConfigureAwait(false);
        }

        if (job.Schedule.RunOnStartup)
        {
            await scheduler.TriggerJob(jobKey, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UnscheduleJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        await scheduler.DeleteJob(new JobKey(jobId.ToString("N"), Group), cancellationToken).ConfigureAwait(false);
    }
}
