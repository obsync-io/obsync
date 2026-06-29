using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The "Job Workspace": a single job's detail page with Overview, Changes, History, Configuration
/// and Logs. Loads the job, its run history, and the latest run's changes and logs.
/// </summary>
public sealed partial class JobDetailViewModel : ObservableObject
{
    private readonly IJobRepository _jobs;
    private readonly IRunRepository _runs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly ISyncEngine _engine;
    private readonly IShellNavigator _navigator;

    private readonly List<SyncRunLog> _allLogs = [];
    private GitRepositoryProfile? _repository;

    [ObservableProperty] private SyncJob? _job;
    [ObservableProperty] private string _connectionName = "—";
    [ObservableProperty] private string _serverName = "—";
    [ObservableProperty] private string _repositoryFullName = "—";
    [ObservableProperty] private string _branch = "—";
    [ObservableProperty] private string _folder = "—";
    [ObservableProperty] private string _scheduleText = "—";
    [ObservableProperty] private string _databasesText = "—";
    [ObservableProperty] private RunStatus? _status;
    [ObservableProperty] private string _lastRunText = "Never run";
    [ObservableProperty] private string _nextRunText = "—";
    [ObservableProperty] private string? _lastCommitSha;
    [ObservableProperty] private string? _lastCommitUrl;
    [ObservableProperty] private bool _showTechnicalLogs;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<SyncRun> Runs { get; } = [];
    public ObservableCollection<ObjectChange> Changes { get; } = [];
    public ObservableCollection<SyncRunLog> Logs { get; } = [];

    public JobDetailViewModel(
        IJobRepository jobs,
        IRunRepository runs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        ISyncEngine engine,
        IShellNavigator navigator)
    {
        _jobs = jobs;
        _runs = runs;
        _connections = connections;
        _repositories = repositories;
        _engine = engine;
        _navigator = navigator;
    }

    public bool HasCommit => !string.IsNullOrEmpty(LastCommitUrl);

    partial void OnLastCommitUrlChanged(string? value) => OnPropertyChanged(nameof(HasCommit));

    partial void OnShowTechnicalLogsChanged(bool value) => RefreshLogs();

    public async Task LoadAsync(Guid jobId)
    {
        var job = await _jobs.GetAsync(jobId);
        Job = job;
        if (job is null)
        {
            return;
        }

        var connection = await _connections.GetAsync(job.ConnectionProfileId);
        _repository = await _repositories.GetAsync(job.RepositoryProfileId);

        ConnectionName = connection?.Name ?? "—";
        ServerName = connection?.ServerName ?? "—";
        RepositoryFullName = _repository?.FullName ?? "—";
        Branch = string.IsNullOrWhiteSpace(job.Branch) ? _repository?.DefaultBranch ?? "—" : job.Branch!;
        Folder = job.DestinationFolder;
        DatabasesText = job.DatabasesDisplay;
        ScheduleText = job.Schedule.Describe();
        Status = job.RunSummary.LastStatus;
        LastRunText = job.RunSummary.LastRunAt is { } at ? at.LocalDateTime.ToString("g") : "Never run";
        NextRunText = job.RunSummary.NextRunAt is { } next ? next.LocalDateTime.ToString("g") : "—";

        var runs = await _runs.GetForJobAsync(jobId, 50);
        Runs.Clear();
        foreach (var run in runs)
        {
            Runs.Add(run);
        }

        var latest = runs.FirstOrDefault();
        LastCommitSha = latest?.CommitSha is { } sha ? sha[..Math.Min(7, sha.Length)] : null;
        LastCommitUrl = latest?.CommitUrl;

        Changes.Clear();
        _allLogs.Clear();
        if (latest is not null)
        {
            foreach (var change in await _runs.GetChangesAsync(latest.Id))
            {
                Changes.Add(change);
            }

            _allLogs.AddRange(await _runs.GetLogsAsync(latest.Id));
        }

        RefreshLogs();
    }

    private void RefreshLogs()
    {
        Logs.Clear();
        foreach (var log in _allLogs.Where(l => ShowTechnicalLogs || l.Level != SyncLogLevel.Debug))
        {
            Logs.Add(log);
        }
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        if (Job is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<SyncProgress>(p => StatusMessage = p.Message);
            var run = await Task.Run(() => _engine.RunJobAsync(Job.Id, RunTrigger.Manual, progress));
            StatusMessage = run.Status switch
            {
                RunStatus.Succeeded => $"Completed — {run.ChangeCount} change(s) pushed.",
                RunStatus.NoChanges => "Completed — no changes.",
                RunStatus.Warning => "Completed with warnings.",
                _ => $"{run.Status}. {run.ErrorMessage}",
            };
            await LoadAsync(Job.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BackAsync() => await _navigator.ShowSectionAsync("Jobs");

    [RelayCommand]
    private void OpenCommit()
    {
        if (!string.IsNullOrEmpty(LastCommitUrl))
        {
            OpenUrl(LastCommitUrl);
        }
    }

    [RelayCommand]
    private void OpenChange(ObjectChange? change)
    {
        if (change is null || _repository is null)
        {
            return;
        }

        OpenUrl(GitHubService.BuildBlobUrl(_repository.Owner, _repository.RepositoryName, Branch, change.RelativePath));
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
