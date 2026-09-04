using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Jobs-page row actions. Deleting must be refused while a run is in progress ANYWHERE — the
/// in-app coordinator covers app runs, the cross-process run lock covers the scheduler service and
/// the CLI. Pausing must persist the disabled state, clear the cached next-run time (so the table
/// never advertises a run that will not happen), and audit; duplicating must produce a paused deep
/// copy under a unique name with a blank run history.
/// </summary>
public sealed class JobsViewModelTests
{
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private JobsViewModel NewViewModel(params SyncJob[] jobs)
    {
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([.. jobs]));
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));

        var settings = Substitute.For<IAppSettingsRepository>();
        settings.GetProductionTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));

        return new JobsViewModel(
            _jobs,
            Substitute.For<IConnectionProfileRepository>(),
            Substitute.For<IRepositoryProfileRepository>(),
            Substitute.For<IJobRunCoordinator>(),
            _audit,
            settings,
            Substitute.For<IJobConfigPorter>(),
            Substitute.For<ISchedulerHealthService>(),
            _clock);
    }

    [Fact]
    public async Task Delete_Refuses_WhileAnotherProcessHoldsTheJobRunLock()
    {
        var job = new SyncJob { Name = "Held elsewhere" };
        var vm = NewViewModel();

        // Simulate a run in the scheduler service / CLI: the cross-process lock is held.
        using (JobRunLock.TryAcquire(ObsyncPaths.LocksRoot, job.Id))
        {
            await vm.DeleteCommand.ExecuteAsync(job);
        }

        await _jobs.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains("run is in progress", vm.StatusMessage);
    }

    [Fact]
    public async Task Delete_Proceeds_WhenNoRunIsInProgress()
    {
        var job = new SyncJob { Name = "Idle" };
        var vm = NewViewModel();

        await vm.DeleteCommand.ExecuteAsync(job);

        await _jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pause_PersistsDisabled_ClearsNextRun_AndAudits()
    {
        var job = new SyncJob
        {
            Name = "Nightly",
            Enabled = true,
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily },
            RunSummary = new JobRunSummary { NextRunAt = DateTimeOffset.UtcNow.AddHours(1) },
        };
        var vm = NewViewModel();

        await vm.TogglePauseCommand.ExecuteAsync(job);

        Assert.False(job.Enabled);
        Assert.Null(job.RunSummary.NextRunAt); // displayed value cleared immediately
        await _jobs.Received(1).UpsertAsync(Arg.Is<SyncJob>(j => j.Id == job.Id && !j.Enabled), Arg.Any<CancellationToken>());
        // UpsertAsync never writes the run summary — the cached next-run must be patched explicitly.
        await _jobs.Received(1).UpdateNextRunAtAsync(job.Id, null, Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            AuditAction.JobPaused, "Job", job.Id.ToString(), "Nightly", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_PreviewsTheNextRun_ForACronScheduleToo()
    {
        // Resume used to hand cron straight to ScheduleProfile.GetNextRun, which cannot answer for
        // it, and wrote the null. The row then showed no next run at all — permanently wherever the
        // service is stopped or not installed, which also blinded the overdue signal, since that
        // rule needs a next-run time to compare against.
        var job = new SyncJob
        {
            Name = "Nightly",
            Enabled = false,
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = "0 0 3 * * ?" },
        };
        var vm = NewViewModel();

        await vm.TogglePauseCommand.ExecuteAsync(job);

        Assert.True(job.Enabled);
        Assert.NotNull(job.RunSummary.NextRunAt);
        Assert.Equal(3, job.RunSummary.NextRunAt!.Value.ToLocalTime().Hour);
        await _jobs.Received(1).UpdateNextRunAtAsync(job.Id, job.RunSummary.NextRunAt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_OfAScheduleItsWindowCanNeverAdmit_LeavesNoNextRun()
    {
        // The other half: resume must not invent a time for a job that cannot run.
        var job = new SyncJob
        {
            Name = "Starved",
            Enabled = false,
            Schedule = new ScheduleProfile
            {
                Kind = ScheduleKind.Weekly,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDay = new TimeOnly(23, 0),
                MaintenanceWindowEnabled = true,
                WindowStart = new TimeOnly(22, 0),
                WindowEnd = new TimeOnly(5, 0),
                DayScope = MaintenanceDayScope.WeekdaysOnly,
            },
        };
        var vm = NewViewModel();

        await vm.TogglePauseCommand.ExecuteAsync(job);

        Assert.True(job.Enabled);
        Assert.Null(job.RunSummary.NextRunAt);
        Assert.True(job.NeverRuns);
    }

    [Fact]
    public async Task Resume_PersistsEnabled_AndAudits()
    {
        var job = new SyncJob
        {
            Name = "Nightly",
            Enabled = false,
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily },
        };
        var vm = NewViewModel();

        await vm.TogglePauseCommand.ExecuteAsync(job);

        Assert.True(job.Enabled);
        Assert.NotNull(job.RunSummary.NextRunAt); // daily cadence previews its next occurrence
        await _jobs.Received(1).UpsertAsync(Arg.Is<SyncJob>(j => j.Id == job.Id && j.Enabled), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            AuditAction.JobResumed, "Job", job.Id.ToString(), "Nightly", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_RefusesAJobThatCannotRunAsItStands()
    {
        // Duplicate was the third route that writes a job and applied none of the rules the wizard
        // and importer do — a JSON round-trip straight to the repository. A job saved before those
        // rules existed could seed a fresh one carrying the same fault.
        var source = new SyncJob
        {
            Name = "Nightly",
            Branch = "main",
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = 48 },
        };
        var vm = NewViewModel(source);
        await vm.LoadAsync();

        await vm.DuplicateCommand.ExecuteAsync(source);

        Assert.Contains("can't be duplicated", vm.StatusMessage);
        Assert.Contains("hourly interval", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        await _jobs.DidNotReceive().UpsertAsync(
            Arg.Is<SyncJob>(j => j.Id != source.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_CreatesAPausedDeepCopy_WithFreshIdentityAndBlankHistory()
    {
        var source = new SyncJob
        {
            Name = "Nightly",
            Enabled = true,
            Databases = ["SalesDB"],
            Tags = ["prod"],
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily },
            RunSummary = new JobRunSummary { LastStatus = RunStatus.Succeeded, LastChangeCount = 42 },
        };
        SyncJob? saved = null;
        _jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var vm = NewViewModel();

        await vm.DuplicateCommand.ExecuteAsync(source);

        Assert.NotNull(saved);
        Assert.NotEqual(source.Id, saved!.Id);
        Assert.Equal("Nightly (copy)", saved.Name);
        Assert.False(saved.Enabled);
        Assert.Null(saved.RunSummary.LastStatus);          // blank history, not the source's
        Assert.Equal(new[] { "SalesDB" }, saved.Databases);
        Assert.NotSame(source.Databases, saved.Databases); // deep copy, not shared references
        Assert.NotSame(source.Schedule, saved.Schedule);
        await _audit.Received(1).WriteAsync(
            AuditAction.JobDuplicated, "Job", saved.Id.ToString(), "Nightly (copy)",
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Contains("paused until you review its schedule", vm.StatusMessage);
    }

    // "name (copy)" first, then "(copy 2)", "(copy 3)", …, skipping any already-taken candidate
    // (case-insensitively) — never a duplicate, never "(copy) (copy)".
    [Theory]
    [InlineData("Job", new string[0], "Job (copy)")]
    [InlineData("Job", new string[] { "Job" }, "Job (copy)")]
    [InlineData("Job", new[] { "Job", "Job (copy)" }, "Job (copy 2)")]
    [InlineData("Job", new[] { "Job", "JOB (COPY)", "job (copy 2)" }, "Job (copy 3)")]
    [InlineData("Job (copy)", new[] { "Job", "Job (copy)" }, "Job (copy) (copy)")]
    public void DuplicateName_IsUnique(string name, string[] existing, string expected) =>
        Assert.Equal(expected, JobsViewModel.DuplicateName(name, existing));
}
