using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>The executive dashboard: key metric cards plus the jobs overview table.</summary>
public sealed partial class DashboardViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IRunRepository _runs;
    private readonly IObjectStateRepository _objectStates;
    private readonly IJobRunCoordinator _coordinator;
    private readonly IShellNavigator _navigator;
    private readonly IAppSettingsRepository _settings;
    private readonly ISchedulerHealthService _schedulerHealth;

    private bool _reloading;

    [ObservableProperty] private int _totalJobs;
    [ObservableProperty] private int _successfulLastRuns;
    [ObservableProperty] private int _failedJobs;
    [ObservableProperty] private int _objectsTracked;
    [ObservableProperty] private string _latestCommit = "—";
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Set when jobs have active schedules the background service cannot execute.</summary>
    [ObservableProperty] private string? _schedulerWarning;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public DashboardViewModel(
        IJobRepository jobs, IConnectionProfileRepository connections, IRepositoryProfileRepository repositories,
        IRunRepository runs, IObjectStateRepository objectStates,
        IJobRunCoordinator coordinator, IShellNavigator navigator,
        IAppSettingsRepository settings, ISchedulerHealthService schedulerHealth)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _runs = runs;
        _objectStates = objectStates;
        _coordinator = coordinator;
        _navigator = navigator;
        _settings = settings;
        _schedulerHealth = schedulerHealth;
        _coordinator.RunStateChanged += OnRunStateChanged;
    }

    private async void OnRunStateChanged(object? sender, Guid jobId)
    {
        RunNowCommand.NotifyCanExecuteChanged();
        if (_reloading)
        {
            return;
        }

        _reloading = true;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not refresh — {ex.Message}";
        }
        finally
        {
            _reloading = false;
        }
    }

    [RelayCommand]
    private async Task OpenAsync(SyncJob? job)
    {
        if (job is not null)
        {
            await _navigator.ShowJobDetailAsync(job.Id, "Dashboard");
        }
    }

    // AllowConcurrentExecutions so different jobs can run at once; CanRun blocks a second run of the
    // SAME job, and the coordinator is the authoritative guard even if a click slips through.
    [RelayCommand(CanExecute = nameof(CanRun), AllowConcurrentExecutions = true)]
    private async Task RunNowAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        try
        {
            var run = await _coordinator.RunAsync(job.Id, RunTrigger.Manual);
            if (run is null)
            {
                StatusMessage = $"{job.Name}: a run is already in progress.";
            }
        }
        catch (InvalidOperationException ex)
        {
            // e.g. the job is already running in another Obsync process (service/CLI run lock).
            StatusMessage = ex.Message;
        }
    }

    private bool CanRun(SyncJob? job) => job is not null && !_coordinator.IsRunning(job.Id);

    public async Task LoadAsync()
    {
        var jobs = await _jobs.GetAllAsync();
        var markers = await _settings.GetProductionTagsAsync();
        await JobDisplay.PopulateAsync(jobs, _connections, _repositories, markers);
        Jobs.Clear();
        foreach (var job in jobs)
        {
            job.IsRunning = _coordinator.IsRunning(job.Id);
            Jobs.Add(job);
        }

        TotalJobs = jobs.Count;
        SuccessfulLastRuns = jobs.Count(j => j.RunSummary.LastStatus is RunStatus.Succeeded or RunStatus.NoChanges);
        FailedJobs = jobs.Count(j => j.RunSummary.LastStatus is RunStatus.Failed);
        ObjectsTracked = await _objectStates.CountAllAsync();

        var recent = await _runs.GetRecentAsync(1);
        var latest = recent.FirstOrDefault(r => r.CommitSha is not null);
        LatestCommit = latest?.CommitSha is { } sha ? sha[..Math.Min(7, sha.Length)] : "—";

        // Warn when a schedule exists that the background service cannot execute — otherwise the
        // "Next Run" column would quietly promise runs that will never happen.
        var health = jobs.Any(SchedulerHealthService.NeedsScheduler)
            ? await _schedulerHealth.GetAsync()
            : null;
        SchedulerWarning = health?.CanExecuteSchedules == false ? health.Summary : null;
    }
}
