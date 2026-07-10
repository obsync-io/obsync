using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>An option in the status filter: a friendly label plus the status it matches (null = all).</summary>
public sealed record StatusFilterOption(string Label, RunStatus? Status);

/// <summary>Recent run history across all jobs, with client-side job / status / text filtering over the
/// most-recent set (no server-side paging).</summary>
public sealed partial class HistoryViewModel : ObservableObject, IAsyncViewModel
{
    private const string AllJobs = "All jobs";

    /// <summary>How many recent runs the page loads; older history stays in the database.</summary>
    private const int MaxRecentRuns = 100;

    /// <summary>A VLDB run can record hundreds of thousands of changes; the diff viewer's list shows
    /// at most this many (the report export always contains the complete list).</summary>
    private const int MaxDiffViewerChanges = 2000;

    /// <summary>How many changed objects a timeline entry shows inline when expanded; the diff
    /// viewer and report export carry the rest.</summary>
    private const int MaxInlineChanges = 100;

    private readonly IRunRepository _runs;
    private readonly IJobRepository _jobs;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IRunReportWriter _reportWriter;
    private readonly IAppSettingsRepository _settings;
    private readonly IClock _clock;

    private bool _reloading;

    [ObservableProperty] private string _selectedJob = AllJobs;
    [ObservableProperty] private StatusFilterOption _selectedStatus;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>True when the load hit the <see cref="MaxRecentRuns"/> cap, i.e. older runs exist
    /// that the page does not show.</summary>
    [ObservableProperty] private bool _isCapped;

    /// <summary>The run selected in the grid; the "Export report" action targets it.</summary>
    [ObservableProperty] private SyncRun? _selectedRun;

    /// <summary>Inline feedback for the "Export report" action (save path or error).</summary>
    [ObservableProperty] private string? _reportMessage;

    /// <summary>The full loaded set; <see cref="RunsView"/> is the filtered projection the grid binds to.</summary>
    public ObservableCollection<SyncRun> Runs { get; } = [];

    /// <summary>Job names to choose from ("All jobs" first, then the distinct names in the loaded set).</summary>
    public ObservableCollection<string> JobNames { get; } = [AllJobs];

    /// <summary>Status filter options ("All statuses" first, then each <see cref="RunStatus"/>).</summary>
    public IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new("All statuses", null),
        new("Succeeded", RunStatus.Succeeded),
        new("No changes", RunStatus.NoChanges),
        new("Warning", RunStatus.Warning),
        new("Failed", RunStatus.Failed),
        new("Running", RunStatus.Running),
        new("Pending", RunStatus.Pending),
        new("Cancelled", RunStatus.Cancelled),
    ];

    public ICollectionView RunsView { get; }

    /// <summary>The timeline projection of the filtered runs, grouped by local day (newest first).</summary>
    public ObservableCollection<TimelineDay> TimelineDays { get; } = [];

    public HistoryViewModel(
        IRunRepository runs,
        IJobRepository jobs,
        IRepositoryProfileRepository repositories,
        IRunReportWriter reportWriter,
        IAppSettingsRepository settings,
        IClock clock,
        IJobRunCoordinator coordinator)
    {
        _runs = runs;
        _jobs = jobs;
        _repositories = repositories;
        _reportWriter = reportWriter;
        _settings = settings;
        _clock = clock;
        _selectedStatus = StatusOptions[0];
        RunsView = CollectionViewSource.GetDefaultView(Runs);
        RunsView.Filter = FilterRun;
        coordinator.RunStateChanged += OnRunStateChanged;
    }

    // A run started or finished in this app — refresh so it appears without leaving and returning.
    private async void OnRunStateChanged(object? sender, Guid jobId)
    {
        if (_reloading)
        {
            return;
        }

        _reloading = true;
        try
        {
            await LoadAsync();
        }
        catch (Exception)
        {
            // Best-effort refresh; the run's outcome is already persisted and the next load shows it.
        }
        finally
        {
            _reloading = false;
        }
    }

    public async Task LoadAsync()
    {
        var runs = await _runs.GetRecentAsync(MaxRecentRuns);
        var markers = await _settings.GetProductionTagsAsync();
        Runs.Clear();
        foreach (var run in runs)
        {
            run.TagChips = JobTags.Classify(run.Tags, markers);
            Runs.Add(run);
        }

        IsCapped = runs.Count == MaxRecentRuns;

        // Rebuild the job-name choices from the loaded set, preserving the current selection if it
        // still exists (otherwise fall back to "All jobs").
        var previous = SelectedJob;
        JobNames.Clear();
        JobNames.Add(AllJobs);
        foreach (var name in runs.Select(r => r.JobName)
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            JobNames.Add(name);
        }

        SelectedJob = JobNames.Contains(previous) ? previous : AllJobs;
        RefreshViews();
    }

    partial void OnSelectedJobChanged(string value) => RefreshViews();

    partial void OnSelectedStatusChanged(StatusFilterOption value) => RefreshViews();

    partial void OnSearchTextChanged(string value) => RefreshViews();

    partial void OnSelectedRunChanged(SyncRun? value)
    {
        ExportReportCommand.NotifyCanExecuteChanged();
        ViewChangesCommand.NotifyCanExecuteChanged();

        // Keep the timeline highlight in step with the grid (both bind to the same run instances).
        foreach (var day in TimelineDays)
        {
            foreach (var entry in day.Entries)
            {
                entry.IsSelected = ReferenceEquals(entry.Run, value);
            }
        }
    }

    // The grid filters live via ICollectionView; the timeline is a projection, so rebuild it from
    // the same filter whenever the inputs change. Expansion state resets by design — the filter
    // changed, so the reader is looking at a different story.
    private void RefreshViews()
    {
        RunsView.Refresh();
        var days = TimelineBuilder.Build(Runs.Where(FilterRun), _clock.UtcNow.ToLocalTime().Date);
        TimelineDays.Clear();
        foreach (var day in days)
        {
            foreach (var entry in day.Entries)
            {
                entry.IsSelected = ReferenceEquals(entry.Run, SelectedRun);
            }

            TimelineDays.Add(day);
        }
    }

    /// <summary>Highlights a timeline entry and makes it the target of the header actions.</summary>
    [RelayCommand]
    private void SelectEntry(TimelineEntry? entry)
    {
        if (entry is not null)
        {
            SelectedRun = entry.Run;
        }
    }

    /// <summary>Expands/collapses a timeline entry, loading its capped change list on first expand.</summary>
    [RelayCommand]
    private async Task ToggleEntryAsync(TimelineEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        SelectedRun = entry.Run;
        if (entry.IsExpanded)
        {
            entry.IsExpanded = false;
            return;
        }

        if (!entry.ChangesLoaded)
        {
            entry.IsLoadingChanges = true;
            try
            {
                var changes = await _runs.GetChangesAsync(entry.Run.Id, MaxInlineChanges);
                entry.Changes.Clear();
                foreach (var change in changes)
                {
                    entry.Changes.Add(new TimelineChange(entry, change));
                }

                entry.TruncationNotice = entry.Run.ChangeCount > changes.Count
                    ? $"Showing the first {changes.Count:N0} of {entry.Run.ChangeCount:N0} changes — open the diff viewer for the rest."
                    : null;
                entry.ChangesLoaded = true;
            }
            finally
            {
                entry.IsLoadingChanges = false;
            }
        }

        entry.IsExpanded = true;
    }

    /// <summary>Opens the diff viewer for a timeline entry's run (first change preselected).</summary>
    [RelayCommand]
    private Task ViewEntryChangesAsync(TimelineEntry? entry) =>
        entry is { CanDiff: true } ? OpenDiffAsync(entry.Run, preselect: null) : Task.CompletedTask;

    /// <summary>Opens the diff viewer preselected at the clicked object change.</summary>
    [RelayCommand]
    private Task OpenTimelineChangeAsync(TimelineChange? change) =>
        change is { Entry.CanDiff: true } ? OpenDiffAsync(change.Entry.Run, change.Change) : Task.CompletedTask;

    private bool CanExportReport() => SelectedRun is not null;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        if (SelectedRun is not { } run)
        {
            return;
        }

        var message = await RunReportExport.PromptAndWriteAsync(_reportWriter, _runs, run);
        if (message is not null)
        {
            ReportMessage = message;
        }
    }

    private bool CanViewChanges() => SelectedRun?.CommitSha is not null;

    [RelayCommand(CanExecute = nameof(CanViewChanges))]
    private Task ViewChangesAsync() =>
        SelectedRun is { CommitSha: not null } run ? OpenDiffAsync(run, preselect: null) : Task.CompletedTask;

    // History does not preload a run's changes, so fetch them (and the job's repository, for the
    // GitHub links) on demand before opening the diff viewer. The preselect is re-found in the
    // fresh list because the viewer matches by instance.
    private async Task OpenDiffAsync(SyncRun run, ObjectChange? preselect)
    {
        var changes = await _runs.GetChangesAsync(run.Id, MaxDiffViewerChanges);
        var job = await _jobs.GetAsync(run.JobId);
        var repository = job?.RepositoryProfileId is { } repositoryId ? await _repositories.GetAsync(repositoryId) : null;
        var match = preselect is null
            ? null
            : changes.FirstOrDefault(c => c.ChangeType == preselect.ChangeType
                && string.Equals(c.RelativePath, preselect.RelativePath, StringComparison.OrdinalIgnoreCase));

        await Views.ScriptDiffWindow.ShowDialogAsync(
            System.Windows.Application.Current?.MainWindow, run, changes, repository, match);
    }

    private bool FilterRun(object item)
    {
        if (item is not SyncRun run)
        {
            return false;
        }

        if (!string.Equals(SelectedJob, AllJobs, StringComparison.Ordinal)
            && !string.Equals(run.JobName, SelectedJob, StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedStatus?.Status is { } status && run.Status != status)
        {
            return false;
        }

        var query = SearchText?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            var inJob = run.JobName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            var inCommit = run.CommitSha?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            var inDatabases = run.Databases?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            var inActor = run.TriggeredBy?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            if (!inJob && !inCommit && !inDatabases && !inActor)
            {
                return false;
            }
        }

        return true;
    }
}
