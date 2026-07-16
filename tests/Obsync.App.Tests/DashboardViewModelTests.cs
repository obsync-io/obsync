using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The dashboard's "Latest Commit" card shows the most recent run that HAS a commit — a newest run
/// without one (failed, no-changes, export-only) must not blank the card.
/// </summary>
public sealed class DashboardViewModelTests
{
    private static DashboardViewModel NewViewModel(IRunRepository runs)
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([]));

        var settings = Substitute.For<IAppSettingsRepository>();
        settings.GetProductionTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));

        return new DashboardViewModel(
            jobs,
            Substitute.For<IConnectionProfileRepository>(),
            Substitute.For<IRepositoryProfileRepository>(),
            runs,
            Substitute.For<IObjectStateRepository>(),
            Substitute.For<IJobRunCoordinator>(),
            Substitute.For<IShellNavigator>(),
            settings,
            Substitute.For<ISchedulerHealthService>(),
            Substitute.For<Obsync.Shared.Abstractions.IClock>());
    }

    private static IRunRepository RepositoryWith(params SyncRun[] runs)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SyncRun>>([.. runs]));
        return repository;
    }

    [Fact]
    public async Task LatestCommit_LooksPastTheNewestRun_WhenItHasNoCommit()
    {
        var vm = NewViewModel(RepositoryWith(
            new SyncRun { JobName = "A", RunKey = "20260715-090000", Status = RunStatus.NoChanges },
            new SyncRun { JobName = "B", RunKey = "20260714-090000", Status = RunStatus.Succeeded, CommitSha = "abcdef0123456789" }));

        await vm.LoadAsync();

        Assert.Equal("abcdef0", vm.LatestCommit);
    }

    [Fact]
    public async Task LatestCommit_ShowsADash_WhenNoRecentRunHasACommit()
    {
        var vm = NewViewModel(RepositoryWith(
            new SyncRun { JobName = "A", RunKey = "20260715-090000", Status = RunStatus.Failed }));

        await vm.LoadAsync();

        Assert.Equal("—", vm.LatestCommit);
    }
}
