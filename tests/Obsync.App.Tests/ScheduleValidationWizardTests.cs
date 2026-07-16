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
/// The wizard's Schedule step rejects configurations that would silently never run: invalid or
/// never-firing cron expressions, and Daily/Weekly times that never intersect an enabled
/// maintenance window. A saved cron job gets a real provisional next-run, not the pre-edit value.
/// </summary>
public sealed class ScheduleValidationWizardTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (CreateJobViewModel Vm, Func<SyncJob?> Saved, Func<DateTimeOffset?> Stamped) BuildVm(
        Guid connectionId, Guid repositoryId)
    {
        SyncJob? saved = null;
        DateTimeOffset? stamped = null;
        var jobs = Substitute.For<IJobRepository>();
        jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        jobs.UpdateNextRunAtAsync(Arg.Any<Guid>(), Arg.Do<DateTimeOffset?>(v => stamped = v), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
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
        clock.UtcNow.Returns(Now);

        var vm = new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(),
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            Substitute.For<Obsync.App.Services.ISchedulerHealthService>());
        return (vm, () => saved, () => stamped);
    }

    private static async Task<CreateJobViewModel> ValidExportJobAsync(
        (CreateJobViewModel Vm, Func<SyncJob?> Saved, Func<DateTimeOffset?> Stamped) built)
    {
        var vm = built.Vm;
        await vm.LoadAsync();
        vm.Name = "Schedule job";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = @"D:\exports";
        return vm;
    }

    [Fact]
    public async Task Save_InvalidCron_BlocksAtScheduleStep()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidExportJobAsync(built);
        vm.SelectedScheduleKind = ScheduleKind.Cron;
        vm.CronExpression = "not a cron";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(4, vm.CurrentStep);
        Assert.Contains("not valid", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_NeverFiringCron_BlocksAtScheduleStep()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidExportJobAsync(built);
        vm.SelectedScheduleKind = ScheduleKind.Cron;
        vm.CronExpression = "0 0 0 30 2 ?"; // February 30th — syntactically valid, never fires

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(4, vm.CurrentStep);
        Assert.Contains("never", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_SwitchingDailyToCron_StampsTheCronNextRun_NotThePreEditValue()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var built = BuildVm(connectionId, repositoryId);
        var vm = built.Vm;
        await vm.LoadAsync();

        var preEditNextRun = Now.AddDays(-3); // the OLD cadence's stale next-run
        var existing = new SyncJob
        {
            Name = "SalesDB Sync",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            Databases = ["db1"],
            Branch = "main",
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(23, 0) },
            RunSummary = new JobRunSummary { NextRunAt = preEditNextRun },
        };
        vm.InitializeForEdit(existing);
        vm.SelectedScheduleKind = ScheduleKind.Cron;
        vm.CronExpression = "0 0 3 * * ?";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(built.Saved());
        var stamped = built.Stamped();
        Assert.NotNull(stamped);
        Assert.True(stamped > Now);
        Assert.NotEqual(preEditNextRun, stamped);
    }

    [Fact]
    public async Task Save_DailyTimeOutsideMaintenanceWindow_Blocks()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidExportJobAsync(built);
        vm.SelectedScheduleKind = ScheduleKind.Daily;
        vm.TimeOfDay = "12:00";
        vm.MaintenanceWindowEnabled = true;
        vm.WindowStart = "22:00";
        vm.WindowEnd = "05:00";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(4, vm.CurrentStep);
        Assert.Contains("maintenance window", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_DailyInsideOvernightMaintenanceWindow_Passes()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidExportJobAsync(built);
        vm.SelectedScheduleKind = ScheduleKind.Daily;
        vm.TimeOfDay = "23:30";
        vm.MaintenanceWindowEnabled = true;
        vm.WindowStart = "22:00";
        vm.WindowEnd = "05:00";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(built.Saved());
        Assert.Equal(new TimeOnly(23, 30), built.Saved()!.Schedule.TimeOfDay);
    }

    [Fact]
    public async Task Save_WeeklyDayIncompatibleWithWindowDayScope_Blocks()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidExportJobAsync(built);
        vm.SelectedScheduleKind = ScheduleKind.Weekly;
        vm.SelectedDayOfWeek = DayOfWeek.Sunday;
        vm.TimeOfDay = "23:00";
        vm.MaintenanceWindowEnabled = true;
        vm.WindowStart = "22:00";
        vm.WindowEnd = "05:00";
        vm.SelectedDayScope = MaintenanceDayScope.WeekdaysOnly; // Sunday 23:00 opens a Sunday window

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(built.Saved());
        Assert.Equal(4, vm.CurrentStep);
        Assert.Contains("never", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_WeeklySundayEarlyMorning_AttributedToSaturdayWindow_Passes()
    {
        var built = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var vm = await ValidExportJobAsync(built);
        vm.SelectedScheduleKind = ScheduleKind.Weekly;
        vm.SelectedDayOfWeek = DayOfWeek.Sunday;
        vm.TimeOfDay = "02:00"; // inside Saturday's 22:00–05:00 overnight window
        vm.MaintenanceWindowEnabled = true;
        vm.WindowStart = "22:00";
        vm.WindowEnd = "05:00";
        vm.SelectedDayScope = MaintenanceDayScope.WeekendsOnly;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(built.Saved());
    }
}
