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

    private bool _reloading;

    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public JobsViewModel(
        IJobRepository jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IJobRunCoordinator coordinator,
        IAuditWriter audit,
        IAppSettingsRepository settings)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _coordinator = coordinator;
        _audit = audit;
        _settings = settings;
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

        var run = await _coordinator.RunAsync(job.Id, RunTrigger.Manual);
        if (run is null)
        {
            StatusMessage = $"{job.Name}: a run is already in progress.";
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
