using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The wizard's reference-data picker: persists checked tables + the row cap, restores them on
/// edit, requires a selection when the toggle is on, and loads tables from the probe while
/// preserving saved entries the loaded database doesn't contain.
/// </summary>
public sealed class ReferenceDataWizardTests
{
    private static (CreateJobViewModel Vm, Func<SyncJob?> Saved, ISqlServerProbe Probe) BuildVm(Guid connectionId, Guid repositoryId)
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

        var probe = Substitute.For<ISqlServerProbe>();
        var vm = new CreateJobViewModel(
            connections, repositories, jobs, probe,
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            Substitute.For<Obsync.App.Services.ISchedulerHealthService>(),
            Substitute.For<Obsync.App.Services.IJobPreflightService>());
        return (vm, () => saved, probe);
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
    public async Task Save_WithReferenceData_PersistsCheckedTablesAndRowCap()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved, _) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId));

        vm.IncludeReferenceData = true;
        vm.ReferenceTables.Add(new SelectableTable("dbo.Currency") { IsSelected = true });
        vm.ReferenceTables.Add(new SelectableTable("dbo.Huge") { IsSelected = false });
        vm.ReferenceDataMaxRows = 250;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        Assert.Equal(["dbo.Currency"], saved()!.Selection.ReferenceDataTables);
        Assert.Equal(250, saved()!.Advanced.ReferenceDataMaxRows);
    }

    [Fact]
    public async Task Save_ToggleOnWithNothingChecked_BlocksTheSave()
    {
        var connectionId = Guid.NewGuid();
        var (vm, saved, _) = BuildVm(connectionId, Guid.NewGuid());
        await vm.LoadAsync();
        vm.InitializeForEdit(ExistingJob(connectionId, Guid.NewGuid()));
        vm.SelectedRepository = vm.Repositories[0];

        vm.IncludeReferenceData = true; // no tables checked

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(saved());
    }

    [Fact]
    public async Task Save_ToggleOff_ClearsTheTableList()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved, _) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        var existing = ExistingJob(connectionId, repositoryId);
        existing.Selection.ReferenceDataTables = ["dbo.Currency"];
        vm.InitializeForEdit(existing);
        Assert.True(vm.IncludeReferenceData); // restored from the job

        vm.IncludeReferenceData = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(saved()!.Selection.ReferenceDataTables);
    }

    [Fact]
    public async Task LoadTables_MergesServerList_WithSavedEntriesFromOtherDatabases()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, _, probe) = BuildVm(connectionId, repositoryId);
        probe.GetTablesAsync(Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), "SalesDB", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<IReadOnlyList<SqlTableInfo>>(
            [
                new SqlTableInfo { Schema = "dbo", Name = "Currency", RowCount = 42 },
                new SqlTableInfo { Schema = "dbo", Name = "Orders", RowCount = 9_000_000 },
            ])));
        await vm.LoadAsync();

        var existing = ExistingJob(connectionId, repositoryId);
        existing.Selection.ReferenceDataTables = ["dbo.Currency", "ref.OnlyInOtherDb"];
        vm.InitializeForEdit(existing);
        vm.TableSourceDatabase = "SalesDB";

        await vm.LoadTablesCommand.ExecuteAsync(null);

        // Server list shown with row counts; the saved check state survived the refresh.
        var currency = vm.ReferenceTables.Single(t => t.QualifiedName == "dbo.Currency");
        Assert.True(currency.IsSelected);
        Assert.Equal(42, currency.RowCount);
        Assert.False(vm.ReferenceTables.Single(t => t.QualifiedName == "dbo.Orders").IsSelected);
        // The saved entry the loaded DB doesn't have is kept (it may exist in another database).
        Assert.True(vm.ReferenceTables.Single(t => t.QualifiedName == "ref.OnlyInOtherDb").IsSelected);
    }
}
