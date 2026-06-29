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

        _logger.LogInformation("Scheduled run starting for job {JobId}.", jobId);
        var run = await _engine.RunJobAsync(jobId, RunTrigger.Scheduled, null, context.CancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Scheduled run for job {JobId} finished with status {Status}.", jobId, run.Status);
    }
}
