using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Deleting a job must be refused while a run is in progress ANYWHERE — the in-app coordinator
/// covers app runs, and the cross-process run lock covers the scheduler service and the CLI.
/// </summary>
public sealed class JobsViewModelTests
{
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private JobsViewModel NewViewModel()
    {
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([]));

        var settings = Substitute.For<IAppSettingsRepository>();
        settings.GetProductionTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));

        return new JobsViewModel(
            _jobs,
            Substitute.For<IConnectionProfileRepository>(),
            Substitute.For<IRepositoryProfileRepository>(),
            Substitute.For<IJobRunCoordinator>(),
            Substitute.For<IAuditWriter>(),
            settings,
            Substitute.For<IJobConfigPorter>(),
            Substitute.For<ISchedulerHealthService>());
    }

    [Fact]
    public async Task Delete_Refuses_WhileAnotherProcessHoldsTheJobRunLock()
    {
        var job = new SyncJob { Name = "Held elsewhere" };
        var vm = NewViewModel();

        // Simulate a run in the scheduler service / CLI: the cross-process lock is held.
        using (JobRunLock.TryAcquire(ObsyncPaths.LocksRoot, job.Id))
        {
            await vm.DeleteCommand.ExecuteAsync(job);
        }

        await _jobs.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains("run is in progress", vm.StatusMessage);
    }

    [Fact]
    public async Task Delete_Proceeds_WhenNoRunIsInProgress()
    {
        var job = new SyncJob { Name = "Idle" };
        var vm = NewViewModel();

        await vm.DeleteCommand.ExecuteAsync(job);

        await _jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
    }
}
