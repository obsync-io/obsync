using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Lists sync jobs and runs them on demand.</summary>
public sealed partial class JobsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IJobRunCoordinator _coordinator;
    private readonly IAuditWriter _audit;
    private readonly IAppSettingsRepository _settings;
    private readonly IJobConfigPorter _porter;
    private readonly ISchedulerHealthService _schedulerHealth;

    private bool _reloading;

    [ObservableProperty] private string? _statusMessage;

    /// <summary>Set when jobs have active schedules the background service cannot execute.</summary>
    [ObservableProperty] private string? _schedulerWarning;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public JobsViewModel(
        IJobRepository jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IJobRunCoordinator coordinator,
        IAuditWriter audit,
        IAppSettingsRepository settings,
        IJobConfigPorter porter,
        ISchedulerHealthService schedulerHealth)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _coordinator = coordinator;
        _audit = audit;
        _settings = settings;
        _porter = porter;
        _schedulerHealth = schedulerHealth;
        _coordinator.RunStateChanged += OnRunStateChanged;
    }

    /// <summary>Imports a job configuration exported from another machine (or a backup).</summary>
    [RelayCommand]
    private async Task ImportJobAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a job configuration",
            Filter = "Obsync job export (*.obsync-job.json)|*.obsync-job.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(dialog.FileName);
            var result = await _porter.ImportAsync(json);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error;
                return;
            }

            StatusMessage = $"Imported \"{result.Job!.Name}\" — review its configuration, then run or enable it.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed — {ex.Message}";
        }
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

        // Warn when a schedule exists that the background service cannot execute — otherwise the
        // "Next Run" column would quietly promise runs that will never happen.
        var health = jobs.Any(SchedulerHealthService.NeedsScheduler)
            ? await _schedulerHealth.GetAsync()
            : null;
        SchedulerWarning = health?.CanExecuteSchedules == false ? health.Summary : null;
    }

    // AllowConcurrentExecutions so different jobs can run at once; CanRun blocks a second run of the
    // SAME job, and the coordinator is the authoritative guard.
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

    [RelayCommand]
    private async Task DeleteAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        if (_coordinator.IsRunning(job.Id))
        {
            StatusMessage = $"{job.Name}: cannot delete while a run is in progress.";
            return;
        }

        await _jobs.DeleteAsync(job.Id);
        await _audit.WriteAsync(AuditAction.JobDeleted, "Job", job.Id.ToString(), job.Name);
        await LoadAsync();
    }
}
