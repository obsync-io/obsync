using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Lists sync jobs and runs them on demand.</summary>
public sealed partial class JobsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IJobRepository _jobs;
    private readonly ISyncEngine _engine;

    [ObservableProperty] private SyncJob? _selectedJob;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public JobsViewModel(IJobRepository jobs, ISyncEngine engine)
    {
        _jobs = jobs;
        _engine = engine;
    }

    public async Task LoadAsync()
    {
        var jobs = await _jobs.GetAllAsync();
        Jobs.Clear();
        foreach (var job in jobs)
        {
            Jobs.Add(job);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunNowAsync(SyncJob? job)
    {
        if (job is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        RunNowCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<SyncProgress>(p => StatusMessage = $"{job.Name}: {p.Message}");
            var run = await Task.Run(() => _engine.RunJobAsync(job.Id, RunTrigger.Manual, progress));
            StatusMessage = run.Status switch
            {
                RunStatus.Succeeded => $"{job.Name}: completed — {run.ChangeCount} change(s) pushed.",
                RunStatus.NoChanges => $"{job.Name}: completed — no changes.",
                RunStatus.Warning => $"{job.Name}: completed with warnings.",
                _ => $"{job.Name}: {run.Status}. {run.ErrorMessage}",
            };
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            RunNowCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand]
    private async Task DeleteAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        await _jobs.DeleteAsync(job.Id);
        await LoadAsync();
    }
}
