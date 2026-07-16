using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>The wizard persists the maintenance window + advanced knobs, and preserves the retry counts.</summary>
public sealed class MaintenanceWindowWizardTests
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
            Substitute.For<Obsync.App.Services.ISchedulerHealthService>(),
            Substitute.For<Obsync.App.Services.IJobPreflightService>());
        return (vm, () => saved);
    }

    [Fact]
    public async Task Save_PersistsWindowAndAdvanced_AndPreservesRetryCounts()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, saved) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();

        var existing = new SyncJob
        {
            Name = "SalesDB Sync",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            Databases = ["db1"],
            Branch = "main",
            Advanced = new JobAdvancedOptions { SqlRetryCount = 7, GitRetryCount = 5 },
        };
        vm.InitializeForEdit(existing);

        vm.SelectedScheduleKind = ScheduleKind.Hourly;
        vm.MaintenanceWindowEnabled = true;
        vm.WindowStart = "22:00";
        vm.WindowEnd = "05:00";
        vm.SelectedDayScope = MaintenanceDayScope.WeekdaysOnly;
        vm.MaxParallelWorkers = 4;
        vm.QueryTimeoutSeconds = 90;
        vm.LockTimeoutSeconds = 30;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved());
        var job = saved()!;
        Assert.True(job.Schedule.MaintenanceWindowEnabled);
        Assert.Equal(new TimeOnly(22, 0), job.Schedule.WindowStart);
        Assert.Equal(new TimeOnly(5, 0), job.Schedule.WindowEnd);
        Assert.Equal(MaintenanceDayScope.WeekdaysOnly, job.Schedule.DayScope);
        Assert.Equal(4, job.Advanced.MaxParallelWorkers);
        Assert.Equal(90, job.Advanced.SqlCommandTimeoutSeconds);
        Assert.Equal(30, job.Advanced.SqlLockTimeoutSeconds);
        Assert.Equal(7, job.Advanced.SqlRetryCount); // unsurfaced knob preserved
        Assert.Equal(5, job.Advanced.GitRetryCount);
    }
}
