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
/// The wizard must persist Export Only with a NULL repository + an export path, while the git modes
/// keep their repository.
/// </summary>
public sealed class ExportModeWizardTests
{
    private static (CreateJobViewModel Vm, Func<SyncJob?> Saved) BuildVm(Guid connectionId, Guid repositoryId)
    {
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
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            Substitute.For<Obsync.App.Services.ISchedulerHealthService>());
        return (vm, () => saved);
    }

    private static SyncJob ExistingJob(Guid connectionId, Guid repositoryId) => new()
    {
        Name = "SalesDB Sync",
        ConnectionProfileId = connectionId,
        RepositoryProfileId = repositoryId,
        Databases = ["db1"],
        Branch = "main",
    };

    [Fact]
    public async Task Save_ExportOnly_PersistsNullRepositoryAndExportPath()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = @"D:\exports\SalesDB.zip";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        Assert.Equal(CommitMode.ExportOnly, saved()!.CommitMode);
        Assert.Null(saved()!.RepositoryProfileId);
        Assert.Null(saved()!.Branch);
        Assert.Equal(@"D:\exports\SalesDB.zip", saved()!.ExportPath);
        Assert.True(vm.IsExportOnly);
    }

    [Fact]
    public async Task Save_ExportOnly_RequiresAnExportPath()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = "   "; // blank

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(saved()); // validation blocked the save
    }

    [Fact]
    public async Task Save_GitMode_KeepsRepositoryAndClearsExportPath()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.SelectedCommitMode = CommitMode.LocalCommitOnly;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(CommitMode.LocalCommitOnly, saved()!.CommitMode);
        Assert.Equal(repositoryId, saved()!.RepositoryProfileId);
        Assert.Null(saved()!.ExportPath);
    }
}
