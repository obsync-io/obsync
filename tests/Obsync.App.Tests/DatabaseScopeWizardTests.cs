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
/// The wizard's "all user databases" scope: the database checklist flips to an exclusion list,
/// the scope round-trips through edit, and a fixed-list job still requires a selection.
/// </summary>
public sealed class DatabaseScopeWizardTests
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

    [Fact]
    public async Task Save_AllUserDatabases_PersistsScopeAndExclusions()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(new SyncJob
        {
            Name = "Estate sync",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            Databases = ["Scratch"],
            Branch = "main",
        });

        vm.SyncAllUserDatabases = true; // the checked database now means "exclude Scratch"

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        Assert.Equal(DatabaseScope.AllUserDatabases, saved()!.DatabaseScope);
        Assert.Empty(saved()!.Databases);
        Assert.Equal(["Scratch"], saved()!.ExcludedDatabases);
    }

    [Fact]
    public async Task Save_AllUserDatabases_AllowsAnEmptyChecklist()
    {
        var connectionId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, Guid.NewGuid());
        await vm.LoadAsync();

        vm.Name = "Estate export";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true; // no databases loaded or checked — valid for this scope
        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = @"D:\exports";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        Assert.Equal(DatabaseScope.AllUserDatabases, saved()!.DatabaseScope);
        Assert.Empty(saved()!.ExcludedDatabases);
        // The dynamic scope's default folder stops at the server — the engine nests per database.
        Assert.Equal("environments/SVR", saved()!.DestinationFolder);
    }

    [Fact]
    public async Task Save_SelectedScope_StillRequiresADatabase()
    {
        var connectionId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, Guid.NewGuid());
        await vm.LoadAsync();

        vm.Name = "Job";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = false; // fixed list, nothing checked

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(saved()); // validation blocked the save
    }

    [Fact]
    public async Task Edit_AllUserDatabasesJob_ShowsExclusionsChecked_AndRoundTrips()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        vm.InitializeForEdit(new SyncJob
        {
            Name = "Estate sync",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            DatabaseScope = DatabaseScope.AllUserDatabases,
            Databases = [],
            ExcludedDatabases = ["Scratch", "TempWork"],
            Branch = "main",
        });

        Assert.True(vm.SyncAllUserDatabases);
        Assert.Equal(["Scratch", "TempWork"], vm.Databases.Where(d => d.IsSelected).Select(d => d.Name));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(DatabaseScope.AllUserDatabases, saved()!.DatabaseScope);
        Assert.Equal(["Scratch", "TempWork"], saved()!.ExcludedDatabases);
    }
}
