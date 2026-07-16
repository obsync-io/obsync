using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
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
    private readonly IClock _clock;

    private bool _reloading;

    [ObservableProperty] private int _totalJobs;
    [ObservableProperty] private int _successfulLastRuns;
    [ObservableProperty] private int _failedJobs;
    [ObservableProperty] private int _objectsTracked;
    [ObservableProperty] private string _latestCommit = "—";
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Set when jobs have active schedules the background service cannot execute.</summary>
    [ObservableProperty] private string? _schedulerWarning;

    /// <summary>"+N more" line under the attention rows when the list is capped; null otherwise.</summary>
    [ObservableProperty] private string? _attentionOverflow;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    /// <summary>Rows of the "Needs attention" card (capped at <see cref="AttentionModel.MaxRows"/>);
    /// the card is absent when this is empty.</summary>
    public ObservableCollection<AttentionItem> AttentionItems { get; } = [];

    public DashboardViewModel(
        IJobRepository jobs, IConnectionProfileRepository connections, IRepositoryProfileRepository repositories,
        IRunRepository runs, IObjectStateRepository objectStates,
        IJobRunCoordinator coordinator, IShellNavigator navigator,
        IAppSettingsRepository settings, ISchedulerHealthService schedulerHealth, IClock clock)
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
        _clock = clock;
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

    /// <summary>Corrective action of a "Needs attention" row: drill into the job, or open the
    /// Servers section for server rows (which carry no job id).</summary>
    [RelayCommand]
    private async Task OpenAttentionAsync(AttentionItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.JobId is { } jobId)
        {
            await _navigator.ShowJobDetailAsync(jobId, "Dashboard");
        }
        else
        {
            await _navigator.ShowSectionAsync("Servers");
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
            var outcome = await _coordinator.RunAsync(job.Id, RunTrigger.Manual);
            if (outcome.Status == RunRequestStatus.AlreadyRunning)
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
        // A stale action message must not outlive a reload (e.g. navigating away and back).
        StatusMessage = null;

        var now = _clock.UtcNow;
        var jobs = await _jobs.GetAllAsync();
        var markers = await _settings.GetProductionTagsAsync();
        await JobDisplay.PopulateAsync(jobs, _connections, _repositories, markers);
        Jobs.Clear();
        foreach (var job in jobs)
        {
            job.IsRunning = _coordinator.IsRunning(job.Id);
            job.IsOverdue = job.IsScheduleOverdue(now);
            Jobs.Add(job);
        }

        TotalJobs = jobs.Count;
        SuccessfulLastRuns = jobs.Count(j => j.RunSummary.LastStatus is RunStatus.Succeeded or RunStatus.NoChanges);
        FailedJobs = jobs.Count(j => j.RunSummary.LastStatus is RunStatus.Failed);
        ObjectsTracked = await _objectStates.CountAllAsync();

        // A window, not just the newest run: the most recent run often has no commit (failed,
        // no-changes, export-only) and the card should still show the latest commit that exists.
        var recent = await _runs.GetRecentAsync(20);
        var latest = recent.FirstOrDefault(r => r.CommitSha is not null);
        LatestCommit = latest?.CommitSha is { } sha ? sha[..Math.Min(7, sha.Length)] : "—";

        // "Needs attention": everything already loaded for the cards feeds it — the recent-run
        // window doubles as the error-quote source, and the server list is one local read.
        var servers = await _connections.GetAllAsync();
        var runErrors = recent
            .Where(r => !string.IsNullOrEmpty(r.ErrorMessage))
            .ToDictionary(r => r.Id, r => r.ErrorMessage!);
        var attention = AttentionModel.Build(jobs, servers, runErrors, now);
        AttentionItems.Clear();
        foreach (var item in attention.Take(AttentionModel.MaxRows))
        {
            AttentionItems.Add(item);
        }

        AttentionOverflow = attention.Count > AttentionModel.MaxRows
            ? $"+{attention.Count - AttentionModel.MaxRows} more"
            : null;

        // Warn when a schedule exists that the background service cannot execute — otherwise the
        // "Next Run" column would quietly promise runs that will never happen.
        var health = jobs.Any(SchedulerHealthService.NeedsScheduler)
            ? await _schedulerHealth.GetAsync()
            : null;
        SchedulerWarning = health?.CanExecuteSchedules == false ? health.Summary : null;
    }
}
