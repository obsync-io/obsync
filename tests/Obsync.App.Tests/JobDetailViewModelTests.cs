using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The Changes tab's "Open in GitHub" action is exposed only where a change actually has a
/// browsable GitHub location: DirectCommit and PullRequest jobs with a repository. Export-only
/// jobs have no repository, and local-commit-only work was never pushed.
/// </summary>
public sealed class JobDetailViewModelTests
{
    private static JobDetailViewModel NewViewModel(IJobRepository jobs, IRepositoryProfileRepository repositories)
    {
        var settings = Substitute.For<IAppSettingsRepository>();
        settings.GetProductionTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var runs = Substitute.For<IRunRepository>();
        runs.GetForJobAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SyncRun>>([]));

        // The detail page binds to the coordinator's shared per-job run state on load.
        var coordinator = Substitute.For<IJobRunCoordinator>();
        coordinator.GetState(Arg.Any<Guid>()).Returns(call => new JobRunState(call.Arg<Guid>()));

        var dependencies = new DependencyExplorerViewModel(
            Substitute.For<IObjectStateRepository>(), Substitute.For<ISqlServerProbe>(), Substitute.For<ICredentialStore>());

        return new JobDetailViewModel(
            jobs,
            runs,
            Substitute.For<IConnectionProfileRepository>(),
            repositories,
            coordinator,
            Substitute.For<IShellNavigator>(),
            Substitute.For<IRunReportWriter>(),
            settings,
            Substitute.For<IJobConfigPorter>(),
            Substitute.For<ISchedulerHealthService>(),
            dependencies);
    }

    private static (IJobRepository Jobs, IRepositoryProfileRepository Repositories) RepositoriesFor(SyncJob job)
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var repositories = Substitute.For<IRepositoryProfileRepository>();
        if (job.RepositoryProfileId is { } repoId)
        {
            repositories.GetAsync(repoId, Arg.Any<CancellationToken>()).Returns(new GitRepositoryProfile
            {
                Id = repoId,
                Name = "Repo",
                Owner = "octo",
                RepositoryName = "scripts",
                DefaultBranch = "main",
            });
        }

        return (jobs, repositories);
    }

    [Fact]
    public async Task OpenInGitHub_IsExposed_ForADirectCommitJobWithARepository()
    {
        var job = new SyncJob
        {
            Name = "Direct",
            CommitMode = CommitMode.DirectCommit,
            RepositoryProfileId = Guid.NewGuid(),
            Databases = ["db"],
        };
        var (jobs, repositories) = RepositoriesFor(job);

        var vm = NewViewModel(jobs, repositories);
        await vm.LoadAsync(job.Id);

        Assert.True(vm.CanOpenChangesInGitHub);
    }

    [Fact]
    public async Task OpenInGitHub_IsHidden_ForAnExportOnlyJob()
    {
        var job = new SyncJob
        {
            Name = "Export",
            CommitMode = CommitMode.ExportOnly,
            ExportPath = @"C:\export",
            Databases = ["db"],
        };
        var (jobs, repositories) = RepositoriesFor(job);

        var vm = NewViewModel(jobs, repositories);
        await vm.LoadAsync(job.Id);

        Assert.False(vm.CanOpenChangesInGitHub);
    }

    [Fact]
    public async Task OpenInGitHub_IsHidden_ForALocalCommitOnlyJob()
    {
        // The commit exists only in the local clone — a github.com link would 404.
        var job = new SyncJob
        {
            Name = "Local",
            CommitMode = CommitMode.LocalCommitOnly,
            RepositoryProfileId = Guid.NewGuid(),
            Databases = ["db"],
        };
        var (jobs, repositories) = RepositoriesFor(job);

        var vm = NewViewModel(jobs, repositories);
        await vm.LoadAsync(job.Id);

        Assert.False(vm.CanOpenChangesInGitHub);
    }
}
