using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.App.Tests;

/// <summary>
/// The diff viewer's object-history rail: lazy loading, version selection re-diffing at that
/// commit, and resets when the selected object changes.
/// </summary>
public sealed class ObjectHistoryViewModelTests
{
    private static readonly string RunSha = new('a', 40);
    private static readonly string OlderSha = new('b', 40);

    private static ObjectChange NewChange(ChangeType changeType, string name = "usp_GetCustomer") => new()
    {
        ChangeType = changeType,
        ObjectType = SqlObjectType.StoredProcedure,
        Schema = "dbo",
        Name = name,
        RelativePath = $"procedures/dbo.{name}.sql",
    };

    private static SyncRun NewRun() => new() { JobName = "Nightly", RunKey = "k", CommitSha = RunSha };

    private static GitRepositoryProfile NewRepository() => new() { Name = "r", Owner = "acme", RepositoryName = "sql" };

    private static IScriptHistoryService NewService()
    {
        var service = Substitute.For<IScriptHistoryService>();
        service.GetVersionsAsync(
                Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ChangeType>(),
                Arg.Any<CancellationToken>())
            .Returns(ScriptVersionsResult.Available("SELECT 1;", "SELECT 2;"));
        service.GetFileHistoryAsync(
                Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ScriptFileHistoryResult.Available(
            [
                new ScriptFileVersion(RunSha, DateTimeOffset.Now, "alice", "latest change"),
                new ScriptFileVersion(OlderSha, DateTimeOffset.Now.AddDays(-3), "bob", "older change"),
            ]));
        return service;
    }

    private static async Task WaitForDiffAsync(ScriptDiffViewModel vm)
    {
        for (var i = 0; i < 100 && vm.IsLoading; i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task TogglingHistory_LoadsTheVersionList_OncePerPath()
    {
        var service = NewService();
        var vm = new ScriptDiffViewModel(service);
        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Modified)], NewRepository(), preselect: null);

        vm.IsHistoryVisible = true;
        Assert.Equal(2, vm.HistoryVersions.Count);
        Assert.Null(vm.HistoryMessage);

        // Hiding and showing again must not re-run git for the same file.
        vm.IsHistoryVisible = false;
        vm.IsHistoryVisible = true;
        await service.Received(1).GetFileHistoryAsync(
            Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectingAVersion_DiffsThatCommit_AsAModification()
    {
        var service = NewService();
        var vm = new ScriptDiffViewModel(service);
        // The run recorded this object as Added — a historical version must still diff as Modified.
        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Added)], NewRepository(), preselect: null);
        vm.IsHistoryVisible = true;

        vm.SelectedVersion = vm.HistoryVersions[1];
        await WaitForDiffAsync(vm);

        await service.Received(1).GetVersionsAsync(
            Arg.Any<GitRepositoryProfile>(), OlderSha, Arg.Any<string>(), ChangeType.Modified,
            Arg.Any<CancellationToken>());
        Assert.NotNull(vm.ViewedVersionText);
        Assert.Contains(OlderSha[..7], vm.ViewedVersionText);
        Assert.True(vm.ShowViewToggle); // split/unified applies to any historical version

        vm.ShowLatestVersionCommand.Execute(null);
        await WaitForDiffAsync(vm);
        Assert.Null(vm.SelectedVersion);
        Assert.Null(vm.ViewedVersionText);
    }

    [Fact]
    public async Task SwitchingObjects_ResetsTheViewedVersion_AndReloadsHistory()
    {
        var service = NewService();
        var first = NewChange(ChangeType.Modified);
        var second = NewChange(ChangeType.Modified, "vw_Orders");
        var vm = new ScriptDiffViewModel(service);
        await vm.LoadAsync(NewRun(), [first, second], NewRepository(), preselect: null);
        vm.IsHistoryVisible = true;
        vm.SelectedVersion = vm.HistoryVersions[1];
        await WaitForDiffAsync(vm);

        vm.SelectedChange = second;
        await WaitForDiffAsync(vm);

        Assert.Null(vm.SelectedVersion);
        Assert.Null(vm.ViewedVersionText);
        // History was re-read for the new file's path.
        await service.Received(1).GetFileHistoryAsync(
            Arg.Any<GitRepositoryProfile>(), second.RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnavailableHistory_ShowsTheReason_InsteadOfAnEmptyRail()
    {
        var service = NewService();
        service.GetFileHistoryAsync(
                Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ScriptFileHistoryResult.Unavailable("This repository hasn't been synced on this machine yet."));
        var vm = new ScriptDiffViewModel(service);
        await vm.LoadAsync(NewRun(), [NewChange(ChangeType.Modified)], NewRepository(), preselect: null);

        vm.IsHistoryVisible = true;

        Assert.Empty(vm.HistoryVersions);
        Assert.Contains("hasn't been synced", vm.HistoryMessage);
    }
}
