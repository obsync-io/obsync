using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The wizard historically never set <see cref="SyncJob.CommitMode"/> at all. These cover that it now
/// persists the selected commit mode and parses the reviewers field for pull-request jobs.
/// </summary>
public sealed class PullRequestWizardTests
{
    private static SyncJob ExistingJob(Guid connectionId, Guid repositoryId) => new()
    {
        Name = "SalesDB Sync",
        ConnectionProfileId = connectionId,
        RepositoryProfileId = repositoryId,
        Databases = ["db1"],
        Branch = "main",
    };

    [Fact]
    public async Task Save_PersistsPullRequestModeAndParsesReviewers()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        SyncJob? saved = null;
        var jobs = Substitute.For<IJobRepository>();
        jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connections = Substitute.For<IConnectionProfileRepository>();
        connections.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SqlConnectionProfile>>(
                [new SqlConnectionProfile { Id = connectionId, Name = "Prod", ServerName = "SVR" }]));
        var repositories = Substitute.For<IRepositoryProfileRepository>();
        repositories.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<GitRepositoryProfile>>(
                [new GitRepositoryProfile { Id = repositoryId, Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" }]));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

        var vm = new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(),
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>());
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.SelectedCommitMode = CommitMode.PullRequest;
        vm.Reviewers = "@alice, bob, Alice"; // '@' stripped, blank/dupe (case-insensitive) removed

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(CommitMode.PullRequest, saved!.CommitMode);
        Assert.Equal(["alice", "bob"], saved.Reviewers);
        Assert.True(vm.IsPullRequest);
    }

    [Fact]
    public async Task Save_DirectMode_LeavesReviewersEmptyEvenIfTyped()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        SyncJob? saved = null;
        var jobs = Substitute.For<IJobRepository>();
        jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connections = Substitute.For<IConnectionProfileRepository>();
        connections.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SqlConnectionProfile>>(
                [new SqlConnectionProfile { Id = connectionId, Name = "Prod", ServerName = "SVR" }]));
        var repositories = Substitute.For<IRepositoryProfileRepository>();
        repositories.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<GitRepositoryProfile>>(
                [new GitRepositoryProfile { Id = repositoryId, Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" }]));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

        var vm = new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(),
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>());
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.SelectedCommitMode = CommitMode.DirectCommit;
        vm.Reviewers = "alice"; // typed but irrelevant in direct mode

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(CommitMode.DirectCommit, saved!.CommitMode);
        Assert.Empty(saved.Reviewers);
    }
}
