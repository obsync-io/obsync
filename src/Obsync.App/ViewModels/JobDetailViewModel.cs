using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
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
    /// <summary>A VLDB run can record hundreds of thousands of changes; the grid shows at most this
    /// many (the report export always contains the complete list).</summary>
    private const int MaxDisplayedChanges = 2000;

    private readonly IJobRepository _jobs;
    private readonly IRunRepository _runs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IJobRunCoordinator _coordinator;
    private readonly IShellNavigator _navigator;
    private readonly IRunReportWriter _reportWriter;
    private readonly IAppSettingsRepository _settings;
    private readonly IJobConfigPorter _porter;
    private readonly ISchedulerHealthService _schedulerHealth;

    /// <summary>The Dependencies tab's own state (object picker + live dependency lookups).</summary>
    public DependencyExplorerViewModel Dependencies { get; }

    private readonly List<SyncRunLog> _allLogs = [];
    private GitRepositoryProfile? _repository;
    private JobRunState? _runState;
    private bool _reloading;

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
    [ObservableProperty] private string? _pullRequestUrl;
    [ObservableProperty] private int? _pullRequestNumber;
    [ObservableProperty] private bool _showTechnicalLogs;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>True after "Cancel run" is clicked, until the run actually stops (disables the button).</summary>
    [ObservableProperty] private bool _isCancelling;

    /// <summary>Inline feedback for the "Export report" action (save path or error).</summary>
    [ObservableProperty] private string? _reportMessage;

    /// <summary>Shown above the Changes grid when the latest run has more changes than the grid
    /// displays (<see cref="MaxDisplayedChanges"/>); null when the full set is shown.</summary>
    [ObservableProperty] private string? _changesNotice;

    /// <summary>The most recent run, used for the Overview status panel (counts, error, timing).</summary>
    [ObservableProperty] private SyncRun? _latestRun;

    /// <summary>The last run's error/warning detail, shown as a banner when the run did not fully succeed.</summary>
    [ObservableProperty] private string? _lastError;

    /// <summary>Set when this job has an active schedule the background service cannot execute.</summary>
    [ObservableProperty] private string? _schedulerWarning;

    /// <summary>True when the last run reported an error or warning worth surfacing.</summary>
    public bool HasError => !string.IsNullOrEmpty(LastError);

    /// <summary>True once at least one run exists, so the Overview can show its result counts.</summary>
    public bool HasLatestRun => LatestRun is not null;

    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnLatestRunChanged(SyncRun? value)
    {
        OnPropertyChanged(nameof(HasLatestRun));
        ExportReportCommand.NotifyCanExecuteChanged();
        ViewDiffCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<SyncRun> Runs { get; } = [];
    public ObservableCollection<ObjectChange> Changes { get; } = [];
    public ObservableCollection<SyncRunLog> Logs { get; } = [];

    public JobDetailViewModel(
        IJobRepository jobs,
        IRunRepository runs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IJobRunCoordinator coordinator,
        IShellNavigator navigator,
        IRunReportWriter reportWriter,
        IAppSettingsRepository settings,
        IJobConfigPorter porter,
        ISchedulerHealthService schedulerHealth,
        DependencyExplorerViewModel dependencies)
    {
        _jobs = jobs;
        _runs = runs;
        _connections = connections;
        _repositories = repositories;
        _coordinator = coordinator;
        _navigator = navigator;
        _reportWriter = reportWriter;
        _settings = settings;
        _porter = porter;
        _schedulerHealth = schedulerHealth;
        Dependencies = dependencies;
    }

    /// <summary>Exports this job's configuration as portable, secret-free JSON.</summary>
    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        if (Job is null)
        {
            return;
        }

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var stem = new string([.. Job.Name.Select(ch => invalid.Contains(ch) ? '_' : ch)]);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{stem}.obsync-job.json",
            Filter = "Obsync job export (*.obsync-job.json)|*.obsync-job.json|All files (*.*)|*.*",
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var json = await _porter.ExportAsync(Job);
            await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
            StatusMessage = $"Configuration exported to {dialog.FileName}. Passwords and tokens are never included.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed — {ex.Message}";
        }
    }

    /// <summary>The section the user drilled in from (Dashboard vs Jobs); "Back" returns here.</summary>
    public string OriginSection { get; set; } = "Jobs";

    /// <summary>True when there is any commit to show (a short SHA), whether or not a URL exists.</summary>
    public bool HasCommit => !string.IsNullOrEmpty(LastCommitSha);

    /// <summary>True when the commit has a browsable URL and should render as a link.</summary>
    public bool HasCommitLink => !string.IsNullOrEmpty(LastCommitUrl);

    /// <summary>True when a SHA exists but no URL, so it is shown as plain (non-link) text.</summary>
    public bool ShowCommitShaOnly => HasCommit && !HasCommitLink;

    partial void OnLastCommitShaChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCommit));
        OnPropertyChanged(nameof(ShowCommitShaOnly));
    }

    partial void OnLastCommitUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCommitLink));
        OnPropertyChanged(nameof(ShowCommitShaOnly));
    }

    /// <summary>True when the last run opened a pull request (renders the "PR #n" link).</summary>
    public bool HasPullRequest => !string.IsNullOrEmpty(PullRequestUrl);

    /// <summary>The PR link label, e.g. "PR #42".</summary>
    public string PullRequestLabel => PullRequestNumber is { } number ? $"PR #{number}" : "Pull request";

    partial void OnPullRequestUrlChanged(string? value) => OnPropertyChanged(nameof(HasPullRequest));

    partial void OnPullRequestNumberChanged(int? value) => OnPropertyChanged(nameof(PullRequestLabel));

    partial void OnShowTechnicalLogsChanged(bool value) => RefreshLogs();

    public async Task LoadAsync(Guid jobId)
    {
        var job = await _jobs.GetAsync(jobId);
        if (job is not null)
        {
            // Classify before assigning Job so the header's tag chips bind with the result in place
            // (SyncJob.TagChips is a plain property and does not raise change notifications).
            job.TagChips = JobTags.Classify(job.Tags, await _settings.GetProductionTagsAsync());
        }

        Job = job;
        if (job is null)
        {
            return;
        }

        var connection = await _connections.GetAsync(job.ConnectionProfileId);
        _repository = job.RepositoryProfileId is { } repoId ? await _repositories.GetAsync(repoId) : null;
        // Computed from Job + the freshly loaded repository, neither of which notifies on its own.
        OnPropertyChanged(nameof(CanOpenChangesInGitHub));
        await Dependencies.InitializeAsync(job, connection);

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

        // Never show a schedule (or next-run time) as live when the background service can't run it.
        var health = SchedulerHealthService.NeedsScheduler(job) ? await _schedulerHealth.GetAsync() : null;
        SchedulerWarning = health?.CanExecuteSchedules == false ? health.Summary : null;

        var runs = await _runs.GetForJobAsync(jobId, 50);
        Runs.Clear();
        foreach (var run in runs)
        {
            Runs.Add(run);
        }

        var latest = runs.FirstOrDefault();
        LatestRun = latest;
        LastError = latest?.Status is RunStatus.Warning or RunStatus.Failed ? latest.ErrorMessage : null;
        LastCommitSha = latest?.CommitSha is { } sha ? sha[..Math.Min(7, sha.Length)] : null;
        LastCommitUrl = latest?.CommitUrl;
        PullRequestUrl = latest?.PullRequestUrl;
        PullRequestNumber = latest?.PullRequestNumber;

        Changes.Clear();
        _allLogs.Clear();
        if (latest is not null)
        {
            foreach (var change in await _runs.GetChangesAsync(latest.Id, MaxDisplayedChanges))
            {
                Changes.Add(change);
            }

            _allLogs.AddRange(await _runs.GetLogsAsync(latest.Id));
        }

        ChangesNotice = latest is not null && latest.ChangeCount > MaxDisplayedChanges
            ? $"Showing the first {MaxDisplayedChanges:N0} of {latest.ChangeCount:N0} changes. Use Export report for the complete list."
            : null;

        RefreshLogs();
        AttachRunState();
    }

    // Bind this view to the job's shared run state so a run in progress (started here or from any
    // other screen) shows live and survives navigation away and back.
    private void AttachRunState()
    {
        DetachRunState();
        if (Job is null)
        {
            return;
        }

        _runState = _coordinator.GetState(Job.Id);
        _runState.PropertyChanged += OnRunStateChanged;
        ApplyRunState();
    }

    /// <summary>Detaches from the shared run state. Called from the view's Unloaded to avoid leaks.</summary>
    public void DetachRunState()
    {
        if (_runState is not null)
        {
            _runState.PropertyChanged -= OnRunStateChanged;
            _runState = null;
        }
    }

    private async void OnRunStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyRunState();

        // When the run finishes, reload so the Overview/History/Changes/Logs reflect the final result.
        if (e.PropertyName == nameof(JobRunState.IsRunning) && _runState is { IsRunning: false }
            && Job is not null && !_reloading)
        {
            _reloading = true;
            try
            {
                await LoadAsync(Job.Id);
            }
            catch
            {
                // A refresh failure is non-fatal; the run itself already recorded its result.
            }
            finally
            {
                _reloading = false;
            }
        }
    }

    private void ApplyRunState()
    {
        IsBusy = _runState?.IsRunning ?? false;
        StatusMessage = _runState?.IsRunning == true ? _runState.Message : null;
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
        if (Job is null)
        {
            return;
        }

        // The coordinator guards against a second concurrent run and owns the shared live state; the
        // run-state subscription drives the live progress and the post-run refresh.
        try
        {
            var outcome = await _coordinator.RunAsync(Job.Id, RunTrigger.Manual);
            if (outcome.Status == RunRequestStatus.AlreadyRunning)
            {
                StatusMessage = "A run is already in progress for this job.";
            }
        }
        catch (InvalidOperationException ex)
        {
            // e.g. the job is already running in another Obsync process (service/CLI run lock).
            StatusMessage = ex.Message;
        }
    }

    private bool CanCancelRun() => IsBusy && !IsCancelling;

    // Fire-and-forget by design: the engine winds down asynchronously and the run-state subscription
    // reports "Cancelling…" and then the final (Cancelled) result.
    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun()
    {
        if (Job is null)
        {
            return;
        }

        IsCancelling = true;
        _coordinator.Cancel(Job.Id);
    }

    partial void OnIsBusyChanged(bool value)
    {
        // The run stopped (cancelled or otherwise) — re-arm the cancel button for the next run.
        if (!value)
        {
            IsCancelling = false;
        }

        CancelRunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCancellingChanged(bool value) => CancelRunCommand.NotifyCanExecuteChanged();

    private bool CanExportReport() => LatestRun is not null;

    // Exports the latest run — the one whose changes and logs the Overview already shows. The export
    // re-fetches the run's complete change and log sets, so it is faithful regardless of the grid's
    // display cap and the technical-logs toggle.
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        if (LatestRun is null)
        {
            return;
        }

        var message = await RunReportExport.PromptAndWriteAsync(_reportWriter, _runs, LatestRun);
        if (message is not null)
        {
            ReportMessage = message;
        }
    }

    [RelayCommand]
    private async Task BackAsync() => await _navigator.ShowSectionAsync(OriginSection);

    [RelayCommand]
    private void OpenCommit()
    {
        if (!string.IsNullOrEmpty(LastCommitUrl))
        {
            OpenUrl(LastCommitUrl);
        }
    }

    [RelayCommand]
    private void OpenPullRequest()
    {
        if (!string.IsNullOrEmpty(PullRequestUrl))
        {
            OpenUrl(PullRequestUrl);
        }
    }

    private bool CanViewDiff(ObjectChange? change) => LatestRun?.CommitSha is not null;

    // The diff comes from the latest run's commit in the local clone; without a CommitSha (failed,
    // no-changes, or export-only run) there is nothing to diff and the row buttons stay disabled.
    [RelayCommand(CanExecute = nameof(CanViewDiff))]
    private async Task ViewDiffAsync(ObjectChange? change)
    {
        if (LatestRun is not { CommitSha: not null } run)
        {
            return;
        }

        await Views.ScriptDiffWindow.ShowDialogAsync(
            System.Windows.Application.Current?.MainWindow, run, [.. Changes], _repository, change);
    }

    /// <summary>True when a change has a browsable GitHub location: DirectCommit and PullRequest
    /// jobs with a repository. Export-only jobs have no repository at all, and local-commit-only
    /// work was never pushed — the button hides for those instead of doing nothing (or lying).</summary>
    public bool CanOpenChangesInGitHub =>
        _repository is not null && Job?.CommitMode is CommitMode.DirectCommit or CommitMode.PullRequest;

    [RelayCommand]
    private void OpenChange(ObjectChange? change)
    {
        if (change is null || _repository is null || !CanOpenChangesInGitHub)
        {
            return;
        }

        // A PR run's change lives on the PR head branch, not the base branch — link it at the
        // run's commit instead (a sha fills the same URL slot as a branch name).
        var reference = Job?.CommitMode == CommitMode.PullRequest && LatestRun?.CommitSha is { } sha
            ? sha
            : Branch;
        OpenUrl(GitHubService.BuildBlobUrl(_repository.Owner, _repository.RepositoryName, reference, change.RelativePath));
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
