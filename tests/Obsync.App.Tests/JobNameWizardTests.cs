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
/// Job names must be unique (History's job filter matches by name): a duplicate is rejected at save,
/// while editing a job that keeps its own name still saves.
/// </summary>
public sealed class JobNameWizardTests
{
    private static (CreateJobViewModel Vm, Func<SyncJob?> Saved) BuildVm(
        Guid connectionId, Guid repositoryId, IReadOnlyList<SyncJob> existingJobs)
    {
        SyncJob? saved = null;
        var jobs = Substitute.For<IJobRepository>();
        jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(existingJobs));

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
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            Substitute.For<Obsync.App.Services.ISchedulerHealthService>(),
            Substitute.For<Obsync.App.Services.IJobPreflightService>());
        return (vm, () => saved);
    }

    [Fact]
    public async Task Save_NameMatchingAnotherJobCaseInsensitively_IsBlocked()
    {
        var connectionId = Guid.NewGuid();
        var existing = new SyncJob { Name = "SalesDB Sync", ConnectionProfileId = connectionId };
        var (vm, saved) = BuildVm(connectionId, Guid.NewGuid(), [existing]);
        await vm.LoadAsync();

        vm.Name = "  salesdb sync "; // differs only in case and padding
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = @"D:\exports";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(saved());
        Assert.Equal(1, vm.CurrentStep);
        Assert.Contains("already exists", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_EditKeepingItsOwnName_IsAllowed()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var existing = new SyncJob
        {
            Name = "SalesDB Sync",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            Databases = ["db1"],
            Branch = "main",
        };
        var (vm, saved) = BuildVm(connectionId, repositoryId, [existing]);
        await vm.LoadAsync();
        vm.InitializeForEdit(existing);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        Assert.Equal("SalesDB Sync", saved()!.Name);
    }
}
