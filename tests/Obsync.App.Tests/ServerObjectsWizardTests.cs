using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The wizard's server-level objects picker: persists the checked types, restores them on edit,
/// defaults to all types on first switch-on, and requires a selection while the toggle is on.
/// </summary>
public sealed class ServerObjectsWizardTests
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
        Databases = ["SalesDB"],
        Branch = "main",
    };

    [Fact]
    public void ServerObjectTypes_OffersTheCatalogServerBand_AndTheMainPickerDoesNot()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());

        var serverBand = SqlObjectTypeCatalog.All.Where(d => d.IsServerScoped).Select(d => d.Type);
        Assert.Equal(serverBand, vm.ServerObjectTypes.Select(t => t.Type));
        Assert.DoesNotContain(vm.ObjectTypes, t => SqlObjectTypeCatalog.Get(t.Type).IsServerScoped);
    }

    [Fact]
    public async Task Save_WithTwoServerTypesChecked_PersistsExactlyThose()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.IncludeServerObjects = true; // first switch-on checks everything…
        foreach (var type in vm.ServerObjectTypes)
        {
            type.IsSelected = type.Type is SqlObjectType.ServerLogin or SqlObjectType.AgentJob;
        }

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        Assert.Equal([SqlObjectType.ServerLogin, SqlObjectType.AgentJob], [.. saved()!.Selection.ServerTypes.Order()]);
    }

    [Fact]
    public void ToggleOnWithNothingChecked_DefaultsToEveryServerType()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());

        vm.IncludeServerObjects = true;

        Assert.All(vm.ServerObjectTypes, t => Assert.True(t.IsSelected));
    }

    [Fact]
    public async Task InitializeForEdit_RestoresTheToggleAndTheSavedSubset()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, _) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        var existing = ExistingJob(connectionId, repositoryId);
        existing.Selection.ServerTypes = [SqlObjectType.LinkedServer];
        vm.InitializeForEdit(existing);

        Assert.True(vm.IncludeServerObjects);
        // The saved subset survives the switch-on — it is not expanded to "all".
        Assert.True(vm.ServerObjectTypes.Single(t => t.Type == SqlObjectType.LinkedServer).IsSelected);
        Assert.Single(vm.ServerObjectTypes, t => t.IsSelected);
    }

    [Fact]
    public async Task Save_ToggleOnWithNothingChecked_BlocksTheSave()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.IncludeServerObjects = true;
        foreach (var type in vm.ServerObjectTypes)
        {
            type.IsSelected = false;
        }

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(saved());
    }

    [Fact]
    public async Task Save_ToggleOff_ClearsTheServerTypes()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        var existing = ExistingJob(connectionId, repositoryId);
        existing.Selection.ServerTypes = [SqlObjectType.ServerLogin, SqlObjectType.AgentJob];
        vm.InitializeForEdit(existing);

        vm.IncludeServerObjects = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(saved()!.Selection.ServerTypes);
    }
}
