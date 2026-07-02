using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>An option in the status filter: a friendly label plus the status it matches (null = all).</summary>
public sealed record StatusFilterOption(string Label, RunStatus? Status);

/// <summary>Recent run history across all jobs, with client-side job / status / text filtering over the
/// most-recent set (no server-side paging).</summary>
public sealed partial class HistoryViewModel : ObservableObject, IAsyncViewModel
{
    private const string AllJobs = "All jobs";

    private readonly IRunRepository _runs;

    [ObservableProperty] private string _selectedJob = AllJobs;
    [ObservableProperty] private StatusFilterOption _selectedStatus;
    [ObservableProperty] private string _searchText = string.Empty;

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

    public HistoryViewModel(IRunRepository runs)
    {
        _runs = runs;
        _selectedStatus = StatusOptions[0];
        RunsView = CollectionViewSource.GetDefaultView(Runs);
        RunsView.Filter = FilterRun;
    }

    public async Task LoadAsync()
    {
        var runs = await _runs.GetRecentAsync(100);
        Runs.Clear();
        foreach (var run in runs)
        {
            Runs.Add(run);
        }

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
        RunsView.Refresh();
    }

    partial void OnSelectedJobChanged(string value) => RunsView.Refresh();

    partial void OnSelectedStatusChanged(StatusFilterOption value) => RunsView.Refresh();

    partial void OnSearchTextChanged(string value) => RunsView.Refresh();

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
            if (!inJob && !inCommit)
            {
                return false;
            }
        }

        return true;
    }
}
