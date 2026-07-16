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
/// The Review step reflects everything the job will actually do: local export path, run-on-startup,
/// the enabled generated files, and the honest no-change behavior (empty heartbeat commit).
/// </summary>
public sealed class ReviewStepWizardTests
{
    private static CreateJobViewModel BuildVm(Guid connectionId, Guid repositoryId)
    {
        var jobs = Substitute.For<IJobRepository>();
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

        return new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(),
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            Substitute.For<Obsync.App.Services.ISchedulerHealthService>(),
            Substitute.For<Obsync.App.Services.IJobPreflightService>());
    }

    private static string ValueOf(CreateJobViewModel vm, string label) =>
        Assert.Single(vm.ReviewItems, i => i.Label == label).Value;

    [Fact]
    public async Task Review_ListsLocalExportPath_Startup_GeneratedFiles_AndHeartbeatText()
    {
        var vm = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        await vm.LoadAsync();
        vm.Name = "Review job";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedRepository = vm.Repositories[0];
        vm.Branch = "main";
        vm.LocalExportPath = @"D:\out";
        vm.RunOnStartup = true;
        vm.RunOnlyIfChanges = false;
        vm.IncludeDatabaseOptions = false;
        vm.IncludeSecurityReview = false;

        vm.CurrentStep = 4;
        vm.NextCommand.Execute(null);

        Assert.Equal(5, vm.CurrentStep);
        Assert.Equal(@"D:\out", ValueOf(vm, "Local export path"));
        Assert.Equal("Yes", ValueOf(vm, "Run on service startup"));
        Assert.Equal("inventory, permissions, documentation", ValueOf(vm, "Generated files"));
        Assert.Equal("Commit even when nothing changed (empty heartbeat commit)", ValueOf(vm, "On changes"));
    }

    [Fact]
    public async Task Review_OmitsOptionalRows_WhenNotConfigured()
    {
        var vm = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        await vm.LoadAsync();
        vm.Name = "Review job";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedRepository = vm.Repositories[0];
        vm.Branch = "main";

        vm.CurrentStep = 4;
        vm.NextCommand.Execute(null);

        Assert.Equal(5, vm.CurrentStep);
        Assert.DoesNotContain(vm.ReviewItems, i => i.Label == "Local export path");
        Assert.DoesNotContain(vm.ReviewItems, i => i.Label == "Run on service startup");
        Assert.Equal("inventory, database options, permissions, documentation, security review",
            ValueOf(vm, "Generated files"));
        Assert.Equal("Commit only when changes are detected", ValueOf(vm, "On changes"));
    }
}
