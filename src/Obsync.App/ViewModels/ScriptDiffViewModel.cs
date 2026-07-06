using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The script &amp; diff viewer for a single run: the run's changed objects on the left, and the
/// selected object's diff (split or unified) on the right, computed from the local git clone.
/// </summary>
public sealed partial class ScriptDiffViewModel : ObservableObject
{
    private readonly IScriptHistoryService _scriptHistory;
    private GitRepositoryProfile? _repository;
    private string? _commitUrl;
    private CancellationTokenSource? _diffCts;

    /// <summary>The in-flight diff load; awaited by <see cref="LoadAsync"/> so the dialog opens with
    /// the first diff already computed instead of flashing a loading state.</summary>
    private Task _diffLoad = Task.CompletedTask;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private ObjectChange? _selectedChange;
    [ObservableProperty] private bool _isSplitView = true;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Why the diff can't be shown (workspace missing, commit absent, git failure); null when fine.</summary>
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private IReadOnlyList<DiffRow> _oldRows = [];
    [ObservableProperty] private IReadOnlyList<DiffRow> _newRows = [];

    /// <summary>The single-pane rows: the unified diff, or the full content for added/deleted objects.</summary>
    [ObservableProperty] private IReadOnlyList<DiffRow> _singleRows = [];

    public ObservableCollection<ObjectChange> Changes { get; } = [];
    public ICollectionView ChangesView { get; }

    public string Title { get; private set; } = "Changed scripts";
    public string? FullSha { get; private set; }
    public string ShortSha { get; private set; } = "—";
    public string RunTimestampText { get; private set; } = string.Empty;

    public ScriptDiffViewModel(IScriptHistoryService scriptHistory)
    {
        _scriptHistory = scriptHistory;
        ChangesView = CollectionViewSource.GetDefaultView(Changes);
        ChangesView.Filter = FilterChange;
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Only a modified object has two versions to lay side by side or unify.</summary>
    public bool ShowViewToggle => !HasError && SelectedChange?.ChangeType == ChangeType.Modified;

    public bool ShowSplit => !IsLoading && !HasError && IsSplitView && SelectedChange?.ChangeType == ChangeType.Modified;

    public bool ShowSingle => !IsLoading && !HasError && !ShowSplit && SelectedChange is not null;

    public bool CanOpenOnGitHub => (_repository is not null && FullSha is not null) || _commitUrl is not null;

    public async Task LoadAsync(
        SyncRun run, IReadOnlyList<ObjectChange> changes, GitRepositoryProfile? repository, ObjectChange? preselect)
    {
        _repository = repository;
        _commitUrl = run.CommitUrl;
        Title = $"Changed scripts — {run.JobName}";
        FullSha = run.CommitSha;
        ShortSha = run.CommitSha is { } sha ? sha[..Math.Min(7, sha.Length)] : "—";
        RunTimestampText = run.StartedAt.LocalDateTime.ToString("g");

        Changes.Clear();
        foreach (var change in changes)
        {
            Changes.Add(change);
        }

        SelectedChange = preselect is not null && changes.Contains(preselect) ? preselect : changes.FirstOrDefault();
        await _diffLoad;
    }

    partial void OnSelectedChangeChanged(ObjectChange? value)
    {
        RaiseViewStateChanged();
        _diffCts?.Cancel();
        var cts = new CancellationTokenSource();
        _diffCts = cts;
        _diffLoad = LoadDiffAsync(value, cts);
    }

    partial void OnIsSplitViewChanged(bool value) => RaiseViewStateChanged();

    partial void OnIsLoadingChanged(bool value) => RaiseViewStateChanged();

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        RaiseViewStateChanged();
    }

    partial void OnFilterTextChanged(string value) => ChangesView.Refresh();

    private void RaiseViewStateChanged()
    {
        OnPropertyChanged(nameof(ShowViewToggle));
        OnPropertyChanged(nameof(ShowSplit));
        OnPropertyChanged(nameof(ShowSingle));
    }

    private async Task LoadDiffAsync(ObjectChange? change, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        OldRows = [];
        NewRows = [];
        SingleRows = [];
        ErrorMessage = null;
        if (change is null)
        {
            return;
        }

        if (_repository is null || FullSha is null)
        {
            ErrorMessage = "This run has no commit in a Git repository, so there is no local history to diff.";
            return;
        }

        IsLoading = true;
        try
        {
            var result = await _scriptHistory.GetVersionsAsync(
                _repository, FullSha, change.RelativePath, change.ChangeType, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsAvailable)
            {
                ErrorMessage = result.UnavailableReason;
                return;
            }

            // Diffing thousands of lines is CPU work; keep it off the UI thread.
            switch (change.ChangeType)
            {
                case ChangeType.Added:
                    // A diff against nothing is noise: show the new script as a plain numbered view.
                    SingleRows = await Task.Run(
                        () => DiffRowMapper.BuildFullContent(result.NewContent, DiffRowKind.Unchanged), ct);
                    break;
                case ChangeType.Deleted:
                    SingleRows = await Task.Run(
                        () => DiffRowMapper.BuildFullContent(result.OldContent, DiffRowKind.Deleted, struck: true), ct);
                    break;
                default:
                    var (oldRows, newRows, unified) = await Task.Run(() =>
                    {
                        var (left, right) = DiffRowMapper.BuildSplit(result.OldContent, result.NewContent);
                        return (left, right, DiffRowMapper.BuildUnified(result.OldContent, result.NewContent));
                    }, ct);
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    OldRows = oldRows;
                    NewRows = newRows;
                    SingleRows = unified;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection; the newer load owns the view state.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The script content could not be loaded — {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_diffCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private void CopySha()
    {
        if (FullSha is not { } sha)
        {
            return;
        }

        try
        {
            Clipboard.SetText(sha);
        }
        catch (ExternalException)
        {
            // The clipboard was briefly locked by another process; a copy is not worth crashing over.
        }
    }

    [RelayCommand]
    private void OpenOnGitHub()
    {
        var url = BuildGitHubUrl();
        if (url is not null)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    // The most specific page GitHub can show: the selected file at this commit, else the commit itself.
    private string? BuildGitHubUrl()
    {
        if (_repository is { } repo && FullSha is { } sha)
        {
            return SelectedChange is { } change
                ? GitHubService.BuildBlobUrl(repo.Owner, repo.RepositoryName, sha, change.RelativePath)
                : GitHubService.BuildCommitUrl(repo.Owner, repo.RepositoryName, sha);
        }

        return _commitUrl;
    }

    private bool FilterChange(object item)
    {
        if (item is not ObjectChange change)
        {
            return false;
        }

        var query = FilterText.Trim();
        return query.Length == 0
            || change.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || change.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
