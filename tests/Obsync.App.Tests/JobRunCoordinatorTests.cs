using NSubstitute;
using Obsync.App.Services;
using Obsync.Engine;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Guards the core fix for concurrent runs: no matter how many screens try to start the same job,
/// only one run executes at a time. Also covers the production run guard on manual runs.
/// </summary>
public sealed class JobRunCoordinatorTests
{
    // A guard that always allows the run (the default NSubstitute bool is false = block).
    private static IProductionRunGuard AllowingGuard()
    {
        var guard = Substitute.For<IProductionRunGuard>();
        guard.ConfirmManualRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    [Fact]
    public async Task RunAsync_RefusesASecondConcurrentRunOfTheSameJob()
    {
        var jobId = Guid.NewGuid();
        var gate = new TaskCompletionSource();

        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(jobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await gate.Task;
                return new SyncRun { JobId = jobId, Status = RunStatus.Succeeded };
            });

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), AllowingGuard());

        // Start the first run (do not await — it blocks on the gate).
        var first = coordinator.RunAsync(jobId, RunTrigger.Manual);
        Assert.True(coordinator.IsRunning(jobId));

        // A second start for the same job while it is running must be refused, not queued or run.
        var second = await coordinator.RunAsync(jobId, RunTrigger.Manual);
        Assert.Null(second);

        // Let the first run finish; it completes normally and clears the running state.
        gate.SetResult();
        var firstResult = await first;
        Assert.NotNull(firstResult);
        Assert.False(coordinator.IsRunning(jobId));

        // The engine was invoked exactly once despite two start requests.
        await engine.Received(1).RunJobAsync(jobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AllowsAnotherRunAfterTheFirstFinishes()
    {
        var jobId = Guid.NewGuid();
        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(jobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new SyncRun { JobId = jobId, Status = RunStatus.NoChanges }));

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), AllowingGuard());

        Assert.NotNull(await coordinator.RunAsync(jobId, RunTrigger.Manual));
        Assert.False(coordinator.IsRunning(jobId));
        Assert.NotNull(await coordinator.RunAsync(jobId, RunTrigger.Manual));
    }

    [Fact]
    public async Task RunAsync_AbortsAndSkipsEngine_WhenTheProductionGuardDeclines()
    {
        var jobId = Guid.NewGuid();
        var engine = Substitute.For<ISyncEngine>();
        var guard = Substitute.For<IProductionRunGuard>();
        guard.ConfirmManualRunAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), guard);

        var result = await coordinator.RunAsync(jobId, RunTrigger.Manual);

        Assert.Null(result);
        Assert.False(coordinator.IsRunning(jobId));
        await engine.DidNotReceive().RunJobAsync(
            Arg.Any<Guid>(), Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_DoesNotConsultTheGuard_ForScheduledRuns()
    {
        var jobId = Guid.NewGuid();
        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(jobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new SyncRun { JobId = jobId, Status = RunStatus.NoChanges }));
        var guard = Substitute.For<IProductionRunGuard>();
        guard.ConfirmManualRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false); // would block if asked

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), guard);

        Assert.NotNull(await coordinator.RunAsync(jobId, RunTrigger.Scheduled));
        await guard.DidNotReceive().ConfirmManualRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
