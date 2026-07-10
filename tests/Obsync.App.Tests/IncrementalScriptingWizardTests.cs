using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>The advanced "Incremental scripting" knob defaults on, round-trips through the wizard, and re-opens visible when off.</summary>
public sealed class IncrementalScriptingWizardTests
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

    private static SyncJob ExistingJob(Guid connectionId, Guid repositoryId, bool incremental) => new()
    {
        Name = "SalesDB Sync",
        ConnectionProfileId = connectionId,
        RepositoryProfileId = repositoryId,
        Databases = ["db1"],
        Branch = "main",
        Advanced = new JobAdvancedOptions { IncrementalScripting = incremental },
    };

    [Fact]
    public async Task Save_DefaultsIncrementalScriptingOn_ForANewJob()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId, incremental: true));

        Assert.True(vm.IncrementalScripting);
        Assert.False(vm.ShowAdvanced); // the default needs no advanced panel

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved()!.Advanced.IncrementalScripting);
    }

    [Fact]
    public async Task Save_PersistsIncrementalScriptingOff()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId, incremental: true));
        vm.IncrementalScripting = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(saved()!.Advanced.IncrementalScripting);
    }

    [Fact]
    public async Task InitializeForEdit_RestoresTheKnob_AndOpensTheAdvancedPanelWhenOff()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, _) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        vm.InitializeForEdit(ExistingJob(connectionId, repositoryId, incremental: false));

        Assert.False(vm.IncrementalScripting);
        Assert.True(vm.ShowAdvanced); // a non-default knob must be visible when re-editing
    }
}
