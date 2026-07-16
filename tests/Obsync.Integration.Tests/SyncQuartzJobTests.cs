using NSubstitute;
using Obsync.Engine;
using Obsync.Scheduler;
using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.Integration.Tests;

/// <summary>
/// Locks the Quartz job-data contract that broke every scheduled run in 0.8.x: Quartz's
/// <c>JobDataMap.GetString</c> THROWS for an absent key, and plain cron fires carry no
/// "trigger" override — so reading it the throwing way killed each scheduled fire before it
/// reached the engine, while startup/catch-up fires (which do carry the key) kept working and
/// masked the breakage. Caught live by the audit's sandboxed-service observation.
/// </summary>
public sealed class SyncQuartzJobTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    private static IJobExecutionContext ContextWith(JobDataMap map)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(map);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task CronFire_WithoutATriggerOverride_RunsAsScheduled()
    {
        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(JobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new SyncRun { JobId = JobId, Status = RunStatus.NoChanges });
        var job = new SyncQuartzJob(engine, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncQuartzJob>.Instance);

        // Exactly what a plain cron trigger delivers: the job id and NOTHING else.
        var map = new JobDataMap { [SyncQuartzJob.JobIdKey] = JobId.ToString() };
        await job.Execute(ContextWith(map));

        await engine.Received(1).RunJobAsync(
            JobId, RunTrigger.Scheduled, Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(nameof(RunTrigger.Startup), RunTrigger.Startup)]
    [InlineData(nameof(RunTrigger.CatchUp), RunTrigger.CatchUp)]
    public async Task TriggerOverride_IsHonored(string overrideValue, RunTrigger expected)
    {
        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(JobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new SyncRun { JobId = JobId, Status = RunStatus.NoChanges });
        var job = new SyncQuartzJob(engine, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncQuartzJob>.Instance);

        var map = new JobDataMap
        {
            [SyncQuartzJob.JobIdKey] = JobId.ToString(),
            [SyncQuartzJob.TriggerKey] = overrideValue,
        };
        await job.Execute(ContextWith(map));

        await engine.Received(1).RunJobAsync(
            JobId, expected, Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>());
    }
}
