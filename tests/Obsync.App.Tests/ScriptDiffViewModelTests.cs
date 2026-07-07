using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.App.Tests;

/// <summary>
/// The diff viewer's view model: selecting a change loads the matching row collections, an
/// unavailable result surfaces the friendly error state, and the entry-point commands stay
/// disabled until the run has a commit to diff.
/// </summary>
public sealed class ScriptDiffViewModelTests
{
    private static readonly string Sha = new('a', 40);

    private static ObjectChange NewChange(ChangeType changeType, string name = "usp_GetCustomer") => new()
    {
        ChangeType = changeType,
        ObjectType = SqlObjectType.StoredProcedure,
        Schema = "dbo",
        Name = name,
        RelativePath = $"procedures/dbo.{name}.sql",
    };

    private static SyncRun NewRun() => new() { JobName = "Nightly", RunKey = "k", CommitSha = Sha };

    private static GitRepositoryProfile NewRepository() => new() { Name = "r", Owner = "acme", RepositoryName = "sql" };

    private static IScriptHistoryService ServiceReturning(ScriptVersionsResult result)
    {
        var service = Substitute.For<IScriptHistoryService>();
        service.GetVersionsAsync(
                Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ChangeType>(),
                Arg.Any<CancellationToken>())
            .Returns(result);
        return service;
    }

    [Fact]
    public async Task ModifiedSelection_LoadsSplitAndUnifiedRows()
    {
        var service = ServiceReturning(ScriptVersionsResult.Available("SELECT 1;", "SELECT 2;"));
        var vm = new ScriptDiffViewModel(service);

        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Modified)], NewRepository(), preselect: null);

        Assert.False(vm.HasError);
        Assert.False(vm.IsLoading);
        Assert.NotEmpty(vm.OldRows);
        Assert.NotEmpty(vm.NewRows);
        Assert.NotEmpty(vm.SingleRows);
        Assert.True(vm.ShowSplit);
        Assert.True(vm.ShowViewToggle);
    }

    [Fact]
    public async Task AddedSelection_ShowsThePlainScriptView_NotADiff()
    {
        var service = ServiceReturning(ScriptVersionsResult.Available(string.Empty, "CREATE VIEW v AS\nSELECT 1;"));
        var vm = new ScriptDiffViewModel(service);

        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Added)], NewRepository(), preselect: null);

        Assert.False(vm.ShowSplit);
        Assert.False(vm.ShowViewToggle);
        Assert.True(vm.ShowSingle);
        Assert.Equal(2, vm.SingleRows.Count);
        Assert.All(vm.SingleRows, r => Assert.Equal(DiffRowKind.Unchanged, r.Kind));
    }

    [Fact]
    public async Task DeletedSelection_ShowsTheOldScriptStruckAsRemoved()
    {
        var service = ServiceReturning(ScriptVersionsResult.Available("DROP ME;", string.Empty));
        var vm = new ScriptDiffViewModel(service);

        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Deleted)], NewRepository(), preselect: null);

        Assert.True(vm.ShowSingle);
        var row = Assert.Single(vm.SingleRows);
        Assert.Equal(DiffRowKind.Deleted, row.Kind);
        Assert.True(row.IsStruck);
    }

    [Fact]
    public async Task UnavailableContent_SurfacesTheReason_InsteadOfCrashing()
    {
        var service = ServiceReturning(ScriptVersionsResult.Unavailable("This repository hasn't been synced on this machine yet."));
        var vm = new ScriptDiffViewModel(service);

        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Modified)], NewRepository(), preselect: null);

        Assert.True(vm.HasError);
        Assert.Contains("hasn't been synced", vm.ErrorMessage);
        Assert.False(vm.ShowSplit);
        Assert.False(vm.ShowSingle);
    }

    [Fact]
    public async Task Preselect_WinsOverTheFirstChange()
    {
        var service = ServiceReturning(ScriptVersionsResult.Available("a", "b"));
        var vm = new ScriptDiffViewModel(service);
        var first = NewChange(ChangeType.Modified, "first");
        var second = NewChange(ChangeType.Modified, "second");

        await vm.LoadAsync(NewRun(), [first, second], NewRepository(), preselect: second);

        Assert.Same(second, vm.SelectedChange);
    }

    [Fact]
    public async Task FilterText_NarrowsTheChangeList_ByNameOrPath()
    {
        var service = ServiceReturning(ScriptVersionsResult.Available("a", "b"));
        var vm = new ScriptDiffViewModel(service);
        await vm.LoadAsync(
            NewRun(), [NewChange(ChangeType.Modified, "usp_GetCustomer"), NewChange(ChangeType.Modified, "vw_Orders")],
            NewRepository(), preselect: null);

        vm.FilterText = "orders";

        // The filter is debounced (~250 ms) so typing doesn't rescan the list per keystroke; wait
        // for the single deferred refresh to have run, then assert.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.ChangesView.Cast<object>().Count() != 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Single(vm.ChangesView.Cast<object>());
    }

    [Fact]
    public void JobDetail_ViewDiff_IsDisabled_UntilTheLatestRunHasACommit()
    {
        var vm = new JobDetailViewModel(
            Substitute.For<IJobRepository>(), Substitute.For<IRunRepository>(),
            Substitute.For<IConnectionProfileRepository>(), Substitute.For<IRepositoryProfileRepository>(),
            Substitute.For<IJobRunCoordinator>(), Substitute.For<IShellNavigator>(),
            Substitute.For<IRunReportWriter>(), Substitute.For<IAppSettingsRepository>(),
            Substitute.For<IJobConfigPorter>(),
            new DependencyExplorerViewModel(
                Substitute.For<IObjectStateRepository>(), Substitute.For<Obsync.Metadata.ISqlServerProbe>(),
                Substitute.For<Obsync.Shared.Abstractions.ICredentialStore>()));
        var change = NewChange(ChangeType.Modified);

        Assert.False(vm.ViewDiffCommand.CanExecute(change));

        vm.LatestRun = new SyncRun { JobName = "j", RunKey = "k" }; // no commit (failed / export-only run)
        Assert.False(vm.ViewDiffCommand.CanExecute(change));

        vm.LatestRun = new SyncRun { JobName = "j", RunKey = "k", CommitSha = Sha };
        Assert.True(vm.ViewDiffCommand.CanExecute(change));
    }
}
