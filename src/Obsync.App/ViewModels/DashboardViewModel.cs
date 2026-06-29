using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>The executive dashboard: key metric cards plus the jobs overview table.</summary>
public sealed partial class DashboardViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IJobRepository _jobs;
    private readonly IRunRepository _runs;
    private readonly IObjectStateRepository _objectStates;

    [ObservableProperty] private int _totalJobs;
    [ObservableProperty] private int _successfulLastRuns;
    [ObservableProperty] private int _failedJobs;
    [ObservableProperty] private int _objectsTracked;
    [ObservableProperty] private string _latestCommit = "—";

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public DashboardViewModel(IJobRepository jobs, IRunRepository runs, IObjectStateRepository objectStates)
    {
        _jobs = jobs;
        _runs = runs;
        _objectStates = objectStates;
    }

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
