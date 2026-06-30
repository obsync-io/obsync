using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>The executive dashboard: key metric cards plus the jobs overview table.</summary>
public sealed partial class DashboardViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IJobRepository _jobs;
    private readonly IRunRepository _runs;
    private readonly IObjectStateRepository _objectStates;
    private readonly ISyncEngine _engine;
    private readonly IShellNavigator _navigator;

    [ObservableProperty] private int _totalJobs;
    [ObservableProperty] private int _successfulLastRuns;
    [ObservableProperty] private int _failedJobs;
    [ObservableProperty] private int _objectsTracked;
    [ObservableProperty] private string _latestCommit = "—";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public DashboardViewModel(
        IJobRepository jobs, IRunRepository runs, IObjectStateRepository objectStates,
        ISyncEngine engine, IShellNavigator navigator)
    {
        _jobs = jobs;
        _runs = runs;
        _objectStates = objectStates;
        _engine = engine;
        _navigator = navigator;
    }

    [RelayCommand]
    private async Task OpenAsync(SyncJob? job)
    {
        if (job is not null)
        {
            await _navigator.ShowJobDetailAsync(job.Id);
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

    public async Task LoadAsync()
    {
        var jobs = await _jobs.GetAllAsync();
        Jobs.Clear();
        foreach (var job in jobs)
        {
            Jobs.Add(job);
        }

        TotalJobs = jobs.Count;
        SuccessfulLastRuns = jobs.Count(j => j.RunSummary.LastStatus is RunStatus.Succeeded or RunStatus.NoChanges);
        FailedJobs = jobs.Count(j => j.RunSummary.LastStatus is RunStatus.Failed);
        ObjectsTracked = await _objectStates.CountAllAsync();

        var recent = await _runs.GetRecentAsync(1);
        var latest = recent.FirstOrDefault(r => r.CommitSha is not null);
        LatestCommit = latest?.CommitSha is { } sha ? sha[..Math.Min(7, sha.Length)] : "—";
    }
}
