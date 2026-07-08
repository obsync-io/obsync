using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>The five "Generated files" toggles default on, round-trip through the wizard, and restore on edit.</summary>
public sealed class GeneratedFilesWizardTests
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
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>());
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
    public async Task Save_DefaultsAllGeneratedFilesOn_ForANewJob()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        await vm.SaveCommand.ExecuteAsync(null);

        var selection = saved()!.Selection;
        Assert.True(selection.IncludeObjectInventory);
        Assert.True(selection.IncludeDatabaseOptions);
        Assert.True(selection.IncludeDatabasePermissionsFile);
        Assert.True(selection.IncludeDocumentation);
        Assert.True(selection.IncludeSecurityReview);
    }

    [Fact]
    public async Task Save_PersistsTogglesOff_AndEditRestoresThem()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.IncludeDocumentation = false;
        vm.IncludeSecurityReview = false;
        await vm.SaveCommand.ExecuteAsync(null);

        var selection = saved()!.Selection;
        Assert.False(selection.IncludeDocumentation);
        Assert.False(selection.IncludeSecurityReview);
        Assert.True(selection.IncludeObjectInventory); // untouched toggles stay on

        // Re-editing the saved job restores the off state into the wizard.
        var (vm2, _) = BuildVm(connectionId, repositoryId);
        await vm2.LoadAsync();
        vm2.InitializeForEdit(saved()!);
        Assert.False(vm2.IncludeDocumentation);
        Assert.False(vm2.IncludeSecurityReview);
        Assert.True(vm2.IncludePermissionsFile);
    }
}
