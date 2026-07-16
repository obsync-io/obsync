using Microsoft.Extensions.Logging;
using Obsync.Engine;
using Obsync.Shared;
using Quartz;

namespace Obsync.Scheduler;

/// <summary>The Quartz job that runs a sync job on its schedule. One job runs at a time.</summary>
[DisallowConcurrentExecution]
public sealed class SyncQuartzJob : IJob
{
    public const string JobIdKey = "jobId";

    /// <summary>
    /// Optional <see cref="RunTrigger"/> name attached when the scheduler fires a job manually
    /// (startup and catch-up runs). Cron fires carry no override and default to Scheduled.
    /// </summary>
    public const string TriggerKey = "trigger";

    private readonly ISyncEngine _engine;
    private readonly ILogger<SyncQuartzJob> _logger;

    public SyncQuartzJob(ISyncEngine engine, ILogger<SyncQuartzJob> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var raw = context.MergedJobDataMap.GetString(JobIdKey);
        if (!Guid.TryParse(raw, out var jobId))
        {
            _logger.LogError("Quartz job is missing a valid '{Key}'.", JobIdKey);
            return;
        }

        // TryGetString, NOT GetString: Quartz's GetString THROWS KeyNotFoundException for an
        // absent key, and plain cron fires carry no trigger override — reading it the throwing
        // way made every scheduled fire die before reaching the engine (startup/catch-up runs,
        // which do carry the key, kept working, masking the breakage). SyncQuartzJobTests locks
        // the absent-key default.
        var trigger = context.MergedJobDataMap.TryGetString(TriggerKey, out var rawTrigger)
            && Enum.TryParse<RunTrigger>(rawTrigger, out var parsed)
                ? parsed
                : RunTrigger.Scheduled;

        _logger.LogInformation("{Trigger} run starting for job {JobId}.", trigger, jobId);
        var run = await _engine.RunJobAsync(jobId, trigger, null, context.CancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Trigger} run for job {JobId} finished with status {Status}.", trigger, jobId, run.Status);
    }
}
