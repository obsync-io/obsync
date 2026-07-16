using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The wizard's clarity additions: database search + select-all/clear on the filtered view, preset
/// transparency from the runtime expansion, commit-mode descriptions, the live folder preview +
/// collision warning, the live next-run preview, service-banner gating by cadence, and the
/// Review-step preflight wiring.
/// </summary>
public sealed class WizardClarityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (CreateJobViewModel Vm, IJobPreflightService Preflight) BuildVm(
        Guid connectionId, Guid repositoryId,
        IReadOnlyList<SyncJob>? existingJobs = null,
        SchedulerHealth? health = null,
        SqlAuthenticationMode authMode = SqlAuthenticationMode.WindowsIntegrated)
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(existingJobs ?? []));

        var connections = Substitute.For<IConnectionProfileRepository>();
        connections.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SqlConnectionProfile>>(
                [new SqlConnectionProfile { Id = connectionId, Name = "Prod", ServerName = "SVR", AuthenticationMode = authMode }]));
        var repositories = Substitute.For<IRepositoryProfileRepository>();
        repositories.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<GitRepositoryProfile>>(
                [new GitRepositoryProfile { Id = repositoryId, Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" }]));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var schedulerHealth = Substitute.For<ISchedulerHealthService>();
        schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            health ?? new SchedulerHealth(SchedulerHealthStatus.Healthy, "healthy"));
        var preflight = Substitute.For<IJobPreflightService>();

        var vm = new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(),
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            schedulerHealth, preflight);
        return (vm, preflight);
    }

    private static List<string> ChangedProperties(CreateJobViewModel vm)
    {
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        return changed;
    }

    // --- Source step: database filter + select all / clear -------------------------------------

    [Fact]
    public void DatabaseFilter_NarrowsTheView_CaseInsensitively()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        vm.Databases.Add(new SelectableDatabase("SalesDB"));
        vm.Databases.Add(new SelectableDatabase("Warehouse"));
        vm.Databases.Add(new SelectableDatabase("salesarchive"));

        vm.DatabaseFilter = "SALES";

        Assert.Equal(["SalesDB", "salesarchive"],
            vm.DatabasesView.Cast<SelectableDatabase>().Select(d => d.Name));
    }

    [Fact]
    public void SelectAllAndClear_ActOnTheFilteredView_NotTheHiddenRows()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        vm.Databases.Add(new SelectableDatabase("SalesDB"));
        vm.Databases.Add(new SelectableDatabase("Warehouse") { IsSelected = true });
        vm.Databases.Add(new SelectableDatabase("SalesArchive"));

        vm.DatabaseFilter = "sales";
        vm.SelectAllDatabasesCommand.Execute(null);

        Assert.True(vm.Databases.Single(d => d.Name == "SalesDB").IsSelected);
        Assert.True(vm.Databases.Single(d => d.Name == "SalesArchive").IsSelected);
        Assert.True(vm.Databases.Single(d => d.Name == "Warehouse").IsSelected); // untouched (hidden)

        vm.ClearDatabaseSelectionCommand.Execute(null);

        Assert.False(vm.Databases.Single(d => d.Name == "SalesDB").IsSelected);
        Assert.True(vm.Databases.Single(d => d.Name == "Warehouse").IsSelected); // still untouched
    }

    // --- Source step: identity caption ----------------------------------------------------------

    [Fact]
    public async Task WindowsAuthNotice_ShownForIntegratedAuth_HiddenForSqlLogin()
    {
        var (windows, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        await windows.LoadAsync();
        windows.SelectedConnection = windows.Connections[0];
        Assert.NotNull(windows.WindowsAuthNotice);
        Assert.Contains("Obsync service account", windows.WindowsAuthNotice);

        var (sqlLogin, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid(), authMode: SqlAuthenticationMode.SqlLogin);
        await sqlLogin.LoadAsync();
        sqlLogin.SelectedConnection = sqlLogin.Connections[0];
        Assert.Null(sqlLogin.WindowsAuthNotice);
    }

    // --- Objects step: preset transparency -------------------------------------------------------

    [Fact]
    public void PresetContents_ComesFromTheRuntimeExpansion_AndRefreshesWithTheSelection()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var changed = ChangedProperties(vm);

        vm.SelectedPreset = ObjectSelectionPreset.ProgrammabilityOnly;

        Assert.Contains(nameof(vm.PresetContents), changed);
        var expected = string.Join(", ", ObjectSelectionPresets.Expand(ObjectSelectionPreset.ProgrammabilityOnly)
            .Select(t => SqlObjectTypeCatalog.Get(t).DisplayName));
        Assert.Equal(expected, vm.PresetContents);

        vm.SelectedPreset = ObjectSelectionPreset.Custom;
        Assert.Equal("Choose object types below.", vm.PresetContents);
    }

    // --- Destination step: mode descriptions, folder preview, collision --------------------------

    [Fact]
    public void CommitModeDescription_SwitchesWithTheSelectedMode()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("Commit and push generated changes directly to the selected branch.", vm.CommitModeDescription);

        vm.SelectedCommitMode = CommitMode.LocalCommitOnly;
        Assert.Equal("Create a local Git commit without pushing to the remote repository.", vm.CommitModeDescription);

        vm.SelectedCommitMode = CommitMode.PullRequest;
        Assert.StartsWith("Pull request:", vm.CommitModeDescription);

        vm.SelectedCommitMode = CommitMode.ExportOnly;
        Assert.StartsWith("No GitHub is contacted.", vm.CommitModeDescription);
    }

    [Fact]
    public async Task FolderPreview_TracksServerDatabaseAndFolderInputs_Live()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        await vm.LoadAsync();
        vm.SelectedConnection = vm.Connections[0];
        vm.Databases.Add(new SelectableDatabase("SalesDB"));

        var changed = ChangedProperties(vm);

        vm.Databases[0].IsSelected = true; // checking a database updates the preview
        Assert.Contains(nameof(vm.FolderPreview), changed);
        Assert.Equal("environments/SVR/SalesDB", vm.FolderPreview);

        vm.SyncAllUserDatabases = true; // dynamic scope stops the default at the server
        Assert.Equal("environments/SVR", vm.FolderPreview);

        vm.DestinationFolder = "custom/folder"; // an explicit folder wins
        Assert.Equal("custom/folder", vm.FolderPreview);
    }

    [Fact]
    public async Task CollisionWarning_NamesTheOtherJob_AndClearsWhenTheFolderDiffers()
    {
        var repositoryId = Guid.NewGuid();
        var other = new SyncJob
        {
            Name = "Nightly estate",
            RepositoryProfileId = repositoryId,
            DestinationFolder = "Environments/SVR", // case differs on purpose
        };
        var (vm, _) = BuildVm(Guid.NewGuid(), repositoryId, existingJobs: [other]);
        await vm.LoadAsync();
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;

        vm.SelectedRepository = vm.Repositories[0]; // preview = environments/SVR → collision

        Assert.NotNull(vm.DestinationCollisionWarning);
        Assert.Contains("Nightly estate", vm.DestinationCollisionWarning);

        vm.DestinationFolder = "environments/SVR-copy";
        Assert.Null(vm.DestinationCollisionWarning);

        vm.DestinationFolder = string.Empty; // back to the colliding default
        Assert.NotNull(vm.DestinationCollisionWarning);

        vm.SelectedCommitMode = CommitMode.ExportOnly; // no repository → no collision concept
        Assert.Null(vm.DestinationCollisionWarning);
    }

    [Fact]
    public async Task CollisionWarning_IgnoresTheJobBeingEdited()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var editing = new SyncJob
        {
            Name = "Myself",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            Databases = ["db1"],
            DestinationFolder = "environments/SVR/db1",
            Branch = "main",
        };
        var (vm, _) = BuildVm(connectionId, repositoryId, existingJobs: [editing]);
        await vm.LoadAsync();

        vm.InitializeForEdit(editing);

        Assert.Null(vm.DestinationCollisionWarning);
    }

    // --- Destination step: branch-name validation ------------------------------------------------

    [Theory]
    [InlineData("feature branch")] // space
    [InlineData("a..b")]
    [InlineData("release.lock")]
    public async Task Save_InvalidBranchName_BlocksAtDestinationStep(string branch)
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, _) = BuildVm(connectionId, repositoryId);
        await vm.LoadAsync();
        vm.Name = "Branch job";
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedRepository = vm.Repositories[0];
        vm.Branch = branch;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.CurrentStep);
        Assert.Contains("not a valid git branch name", vm.StatusMessage);
    }

    // --- Schedule step: live next-run preview + banner gating ------------------------------------

    [Fact]
    public void NextRunPreview_ChangesWithTheCadence_AndExplainsManual()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        var changed = ChangedProperties(vm);

        Assert.Equal("Runs only when you start it manually.", vm.NextRunPreview);

        vm.SelectedScheduleKind = ScheduleKind.Daily;
        Assert.Contains(nameof(vm.NextRunPreview), changed);
        Assert.StartsWith("Next run:", vm.NextRunPreview);
        Assert.Contains(TimeZoneInfo.Local.DisplayName, vm.NextRunPreview);

        changed.Clear();
        vm.TimeOfDay = "07:15"; // editing the time re-computes the preview
        Assert.Contains(nameof(vm.NextRunPreview), changed);
        Assert.Contains("7:15", vm.NextRunPreview); // culture-tolerant: "07:15" or "7:15 AM"
    }

    [Fact]
    public void NextRunPreview_IsAbsentForAnInvalidCron()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        vm.SelectedScheduleKind = ScheduleKind.Cron;
        vm.CronExpression = "not a cron";

        Assert.Null(vm.NextRunPreview);

        vm.CronExpression = "0 0 3 * * ?";
        Assert.StartsWith("Next run:", vm.NextRunPreview);
    }

    [Fact]
    public async Task ServiceBanner_OnlyShowsWhenTheCadenceNeedsTheScheduler()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid(),
            health: new SchedulerHealth(SchedulerHealthStatus.NotRunning, "The service is stopped."));
        await vm.LoadAsync();

        Assert.NotNull(vm.ScheduleServiceNotice);
        Assert.False(vm.ShowScheduleServiceNotice); // Manual without startup needs no service

        vm.SelectedScheduleKind = ScheduleKind.Daily;
        Assert.True(vm.ShowScheduleServiceNotice);

        vm.SelectedScheduleKind = ScheduleKind.Manual;
        Assert.False(vm.ShowScheduleServiceNotice);

        vm.RunOnStartup = true; // startup runs need the service even on a manual cadence
        Assert.True(vm.ShowScheduleServiceNotice);
    }

    [Fact]
    public async Task ServiceBanner_StaysHiddenWhenTheSchedulerIsHealthy()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        await vm.LoadAsync();
        vm.SelectedScheduleKind = ScheduleKind.Daily;

        Assert.Null(vm.ScheduleServiceNotice);
        Assert.False(vm.ShowScheduleServiceNotice);
    }

    [Fact]
    public void OverlapNote_HiddenForManual_ShownForScheduledCadences()
    {
        var (vm, _) = BuildVm(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(vm.ShowOverlapNote);
        vm.SelectedScheduleKind = ScheduleKind.Hourly;
        Assert.True(vm.ShowOverlapNote);
    }

    // --- Review step: preflight wiring ------------------------------------------------------------

    [Fact]
    public async Task RunPreflight_PassesTheDraftState_AndPublishesTheResults()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var (vm, preflight) = BuildVm(connectionId, repositoryId);
        JobPreflightRequest? seen = null;
        preflight.RunAsync(Arg.Do<JobPreflightRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiagnosticResult>>(
                [new DiagnosticResult("SQL connection", DiagnosticStatus.Pass, "ok", DateTimeOffset.UtcNow)]));
        await vm.LoadAsync();
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedRepository = vm.Repositories[0];
        vm.Branch = "main";

        await vm.RunPreflightCommand.ExecuteAsync(null);

        Assert.NotNull(seen);
        Assert.Equal(connectionId, seen!.Connection?.Id);
        Assert.Equal(repositoryId, seen.Repository?.Id);
        Assert.Equal("main", seen.Branch);
        Assert.Equal("environments/SVR", seen.EffectiveFolder);
        var result = Assert.Single(vm.PreflightResults);
        Assert.Equal("SQL connection", result.Name);
        Assert.False(vm.IsPreflightRunning);
    }

    [Fact]
    public async Task RunPreflight_ExportOnly_SendsNoRepository()
    {
        var (vm, preflight) = BuildVm(Guid.NewGuid(), Guid.NewGuid());
        JobPreflightRequest? seen = null;
        preflight.RunAsync(Arg.Do<JobPreflightRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiagnosticResult>>([]));
        await vm.LoadAsync();
        vm.SelectedConnection = vm.Connections[0];
        vm.SyncAllUserDatabases = true;
        vm.SelectedRepository = vm.Repositories[0]; // selected, but Export Only must not send it
        vm.SelectedCommitMode = CommitMode.ExportOnly;
        vm.ExportPath = @"D:\exports";

        await vm.RunPreflightCommand.ExecuteAsync(null);

        Assert.NotNull(seen);
        Assert.Null(seen!.Repository);
        Assert.Equal(@"D:\exports", seen.ExportPath);
    }
}
