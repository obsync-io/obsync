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
/// The Destination step rejects paths that would only fail at run time: a repo-relative folder must
/// stay relative and inside the repository ('..' escapes, drive letters, invalid characters), and
/// export paths must be full paths.
/// </summary>
public sealed class DestinationValidationWizardTests
{
    private static (CreateJobViewModel Vm, Func<SyncJob?> Saved) BuildVm(Guid connectionId, Guid repositoryId)
    {
        SyncJob? saved = null;
        var jobs = Substitute.For<IJobRepository>();
        jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([]));

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

    private static async Task<CreateJobViewModel> ValidGitJobAsync((CreateJobViewModel Vm, Func<SyncJob?> Saved) built)
    {
        var vm = built.Vm;
        await vm.LoadAsync();
        vm.Name = "Destination job";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedRepository = vm.Repositories[0];
        vm.Branch = "main";
        return vm;
    }

    [Theory]
    [InlineData("environments/../secrets")] // parent traversal escapes the repository
    [InlineData(@"..\outside")]
    [InlineData(@"C:\absolute\folder")] // repo-relative folders must not be rooted
    [InlineData("environments/pro|d")] // invalid path character
    public async Task Save_BadDestinationFolder_IsBlocked(string folder)
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidGitJobAsync(built);
        vm.DestinationFolder = folder;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(3, vm.CurrentStep);
        Assert.Contains("relative path inside the repository", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_RelativeDestinationFolder_Passes()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidGitJobAsync(built);
        vm.DestinationFolder = "environments/prod/SalesDB";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(built.Saved());
        Assert.Equal("environments/prod/SalesDB", built.Saved()!.DestinationFolder);
    }

    [Theory]
    [InlineData(@"exports\relative")] // must be a full path
    [InlineData(@"D:\exports\bad|name.zip")] // invalid path character
    public async Task Save_BadExportPath_IsBlocked(string exportPath)
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidGitJobAsync(built);
        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = exportPath;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(3, vm.CurrentStep);
        Assert.Contains("full path", vm.StatusMessage);
    }

    [Theory]
    [InlineData(@"D:\exports")]
    [InlineData(@"\\server\share\SalesDB.zip")] // UNC allowed
    public async Task Save_RootedExportPath_Passes(string exportPath)
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidGitJobAsync(built);
        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = exportPath;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(built.Saved());
        Assert.Equal(exportPath, built.Saved()!.ExportPath);
    }

    [Fact]
    public async Task Save_RelativeLocalExportPath_IsBlocked()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidGitJobAsync(built);
        vm.LocalExportPath = @"local\out";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(3, vm.CurrentStep);
        Assert.Contains("local export path", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_RootedLocalExportPath_Passes()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidGitJobAsync(built);
        vm.LocalExportPath = @"D:\out";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(built.Saved());
        Assert.Equal(@"D:\out", built.Saved()!.LocalExportPath);
    }
}
