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
/// only one run executes at a time. Also covers the production run guard on manual runs, the
/// distinct Declined/AlreadyRunning outcomes, and cancelling an in-flight run.
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
        Assert.True(coordinator.HasActiveRuns);

        // A second start for the same job while it is running must be refused, not queued or run.
        var second = await coordinator.RunAsync(jobId, RunTrigger.Manual);
        Assert.Equal(RunRequestStatus.AlreadyRunning, second.Status);
        Assert.Null(second.Run);

        // Let the first run finish; it completes normally and clears the running state.
        gate.SetResult();
        var firstResult = await first;
        Assert.Equal(RunRequestStatus.Started, firstResult.Status);
        Assert.NotNull(firstResult.Run);
        Assert.False(coordinator.IsRunning(jobId));
        Assert.False(coordinator.HasActiveRuns);

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

        Assert.Equal(RunRequestStatus.Started, (await coordinator.RunAsync(jobId, RunTrigger.Manual)).Status);
        Assert.False(coordinator.IsRunning(jobId));
        Assert.Equal(RunRequestStatus.Started, (await coordinator.RunAsync(jobId, RunTrigger.Manual)).Status);
    }

    [Fact]
    public async Task RunAsync_ReturnsDeclinedAndSkipsEngine_WhenTheProductionGuardDeclines()
    {
        var jobId = Guid.NewGuid();
        var engine = Substitute.For<ISyncEngine>();
        var guard = Substitute.For<IProductionRunGuard>();
        guard.ConfirmManualRunAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), guard);

        var result = await coordinator.RunAsync(jobId, RunTrigger.Manual);

        // Declined must be distinguishable from AlreadyRunning — the UI shows a message only for the latter.
        Assert.Equal(RunRequestStatus.Declined, result.Status);
        Assert.Null(result.Run);
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

        Assert.Equal(RunRequestStatus.Started, (await coordinator.RunAsync(jobId, RunTrigger.Scheduled)).Status);
        await guard.DidNotReceive().ConfirmManualRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_SignalsTheEngineToken_AndReportsCancelling()
    {
        var jobId = Guid.NewGuid();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var engineToken = CancellationToken.None;

        // A fake engine that mirrors the real one: it observes the token and records Cancelled
        // instead of throwing, finishing only when the test releases it.
        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(jobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                engineToken = call.ArgAt<CancellationToken>(3);
                started.SetResult();
                await release.Task;
                return new SyncRun
                {
                    JobId = jobId,
                    Status = engineToken.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Succeeded,
                };
            });

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), AllowingGuard());

        var pending = coordinator.RunAsync(jobId, RunTrigger.Manual);
        await started.Task;

        coordinator.Cancel(jobId);

        // The cancel reached the very token the engine received. (The "Cancelling…" message is set
        // through the UI dispatcher, which does not pump in unit tests, so it is not asserted here.)
        Assert.True(engineToken.IsCancellationRequested);

        release.SetResult();
        var outcome = await pending;
        Assert.Equal(RunRequestStatus.Started, outcome.Status);
        Assert.Equal(RunStatus.Cancelled, outcome.Run!.Status);
        Assert.False(coordinator.IsRunning(jobId));
    }

    [Fact]
    public async Task Cancel_IsANoOp_WhenTheJobIsNotRunning()
    {
        var jobId = Guid.NewGuid();
        var engine = Substitute.For<ISyncEngine>();
        engine.RunJobAsync(jobId, Arg.Any<RunTrigger>(), Arg.Any<IProgress<SyncProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new SyncRun
            {
                JobId = jobId,
                Status = call.ArgAt<CancellationToken>(3).IsCancellationRequested ? RunStatus.Cancelled : RunStatus.NoChanges,
            }));

        var coordinator = new JobRunCoordinator(engine, Substitute.For<IAuditWriter>(), AllowingGuard());

        coordinator.Cancel(jobId); // never ran — must not throw or create state

        // A stale cancel does not poison the next run of the same job.
        var outcome = await coordinator.RunAsync(jobId, RunTrigger.Manual);
        Assert.Equal(RunRequestStatus.Started, outcome.Status);
        Assert.Equal(RunStatus.NoChanges, outcome.Run!.Status);
    }
}
