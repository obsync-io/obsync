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
    /// <summary>Debounce for the filter box, so typing over a large change list re-filters once the
    /// user pauses instead of scanning the whole list on every keystroke.</summary>
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the "copied" confirmation stays on screen.</summary>
    private static readonly TimeSpan CopyStatusDuration = TimeSpan.FromSeconds(2);

    private readonly IScriptHistoryService _scriptHistory;
    private GitRepositoryProfile? _repository;
    private string? _commitUrl;
    private CancellationTokenSource? _diffCts;
    private CancellationTokenSource? _filterCts;
    private int _addedCount;
    private int _modifiedCount;
    private int _deletedCount;
    private int _copyStatusVersion;
    private IReadOnlyList<int> _findMatches = [];
    private int _findPosition = -1;

    /// <summary>The in-flight diff load; awaited by <see cref="LoadAsync"/> so the dialog opens with
    /// the first diff already computed instead of flashing a loading state.</summary>
    private Task _diffLoad = Task.CompletedTask;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private ObjectChange? _selectedChange;
    [ObservableProperty] private bool _isSplitView = true;
    [ObservableProperty] private bool _isLoading;

    /// <summary>The change-type chip filter over the change list; null shows all types.</summary>
    [ObservableProperty] private ChangeType? _typeFilter;

    /// <summary>Wraps long diff lines instead of scrolling them horizontally.</summary>
    [ObservableProperty] private bool _isWordWrap;

    /// <summary>Transient "copied" confirmation shown in the diff header; clears itself.</summary>
    [ObservableProperty] private string? _copyStatus;

    /// <summary>The script text a copy targets: the new side for added/modified (and historical
    /// versions), the old content for deleted. Null while loading or when the diff is unavailable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyScriptCommand))]
    private string? _viewedScriptText;

    // --- Find within the viewed script (row-level; matches the visible side) -------------------

    [ObservableProperty] private string _findText = string.Empty;

    /// <summary>The row the find is currently on; the window scrolls/selects it in the visible pane.</summary>
    [ObservableProperty] private DiffRow? _findCurrentRow;

    // --- Object history (every committed version of the selected file, from the local clone) ----

    [ObservableProperty] private bool _isHistoryVisible;
    [ObservableProperty] private bool _isHistoryLoading;
    [ObservableProperty] private IReadOnlyList<ScriptFileVersion> _historyVersions = [];

    /// <summary>The version being viewed; null means the run's own commit (the latest the run saw).</summary>
    [ObservableProperty] private ScriptFileVersion? _selectedVersion;

    /// <summary>Why the history rail is empty (clone missing, no commits); null when versions show.</summary>
    [ObservableProperty] private string? _historyMessage;

    /// <summary>The path the loaded history belongs to, so re-toggling never re-runs git for nothing.</summary>
    private string? _historyPath;

    /// <summary>Why the diff can't be shown (workspace missing, commit absent, git failure); null when fine.</summary>
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private IReadOnlyList<DiffRow> _oldRows = [];
    [ObservableProperty] private IReadOnlyList<DiffRow> _newRows = [];

    /// <summary>The single-pane rows: the unified diff, or the full content for added/deleted objects.</summary>
    [ObservableProperty] private IReadOnlyList<DiffRow> _singleRows = [];

    /// <summary>The filterable change list. Rebuilt as one view over the already-populated list on
    /// each load, so a large change set costs a single notification instead of one per item.</summary>
    [ObservableProperty] private ICollectionView _changesView;

    public string Title { get; private set; } = "Changed scripts";
    public string? FullSha { get; private set; }
    public string ShortSha { get; private set; } = "—";
    public string RunTimestampText { get; private set; } = string.Empty;

    public ScriptDiffViewModel(IScriptHistoryService scriptHistory)
    {
        _scriptHistory = scriptHistory;
        _changesView = CreateChangesView([]);
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>A historical version always diffs against its parent, so it renders as a modification.</summary>
    private bool ViewedAsModified => SelectedVersion is not null || SelectedChange?.ChangeType == ChangeType.Modified;

    /// <summary>Only a modified object has two versions to lay side by side or unify.</summary>
    public bool ShowViewToggle => !HasError && ViewedAsModified;

    public bool ShowSplit => !IsLoading && !HasError && IsSplitView && ViewedAsModified;

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

        _addedCount = changes.Count(c => c.ChangeType == ChangeType.Added);
        _modifiedCount = changes.Count(c => c.ChangeType == ChangeType.Modified);
        _deletedCount = changes.Count(c => c.ChangeType == ChangeType.Deleted);
        OnPropertyChanged(nameof(AllChipLabel));
        OnPropertyChanged(nameof(AddedChipLabel));
        OnPropertyChanged(nameof(ModifiedChipLabel));
        OnPropertyChanged(nameof(DeletedChipLabel));

        ChangesView = CreateChangesView([.. changes]);

        SelectedChange = preselect is not null && changes.Contains(preselect) ? preselect : changes.FirstOrDefault();
        await _diffLoad;
    }

    // Chip captions carry the run's per-type totals (not the filtered view's) so the counts stay
    // stable while the user filters.
    public string AllChipLabel => $"All {_addedCount + _modifiedCount + _deletedCount:N0}";
    public string AddedChipLabel => $"Added {_addedCount:N0}";
    public string ModifiedChipLabel => $"Modified {_modifiedCount:N0}";
    public string DeletedChipLabel => $"Deleted {_deletedCount:N0}";

    partial void OnTypeFilterChanged(ChangeType? value) => ChangesView.Refresh();

    private ListCollectionView CreateChangesView(List<ObjectChange> changes) =>
        new(changes) { Filter = FilterChange };

    partial void OnSelectedChangeChanged(ObjectChange? value)
    {
        // A new object always starts at the run's own version. Reset via the backing field so the
        // partial hook doesn't schedule a second, redundant diff load.
        _selectedVersion = null;
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(ViewedVersionText));

        RaiseViewStateChanged();
        _diffCts?.Cancel();
        var cts = new CancellationTokenSource();
        _diffCts = cts;
        _diffLoad = LoadDiffAsync(value, cts);

        if (IsHistoryVisible)
        {
            _ = LoadHistoryAsync();
        }
    }

    partial void OnSelectedVersionChanged(ScriptFileVersion? value)
    {
        OnPropertyChanged(nameof(ViewedVersionText));
        RaiseViewStateChanged();
        _diffCts?.Cancel();
        var cts = new CancellationTokenSource();
        _diffCts = cts;
        _diffLoad = LoadDiffAsync(SelectedChange, cts);
    }

    partial void OnIsHistoryVisibleChanged(bool value)
    {
        if (value)
        {
            _ = LoadHistoryAsync();
        }
    }

    /// <summary>"Viewing a1b2c3d · 05/07/2026 14:03" when a historical version is selected; null at latest.</summary>
    public string? ViewedVersionText =>
        SelectedVersion is { } version ? $"Viewing {version.ShortSha} · {version.Date.LocalDateTime:g}" : null;

    /// <summary>Returns to the run's own version of the selected object.</summary>
    [RelayCommand]
    private void ShowLatestVersion() => SelectedVersion = null;

    private async Task LoadHistoryAsync()
    {
        if (SelectedChange is not { } change || _repository is null)
        {
            HistoryVersions = [];
            HistoryMessage = _repository is null
                ? "This run has no Git repository, so there is no committed history."
                : null;
            return;
        }

        if (string.Equals(_historyPath, change.RelativePath, StringComparison.Ordinal))
        {
            return;
        }

        IsHistoryLoading = true;
        try
        {
            var result = await _scriptHistory.GetFileHistoryAsync(_repository, change.RelativePath);
            _historyPath = change.RelativePath;
            HistoryVersions = result.Versions;
            HistoryMessage = !result.IsAvailable
                ? result.UnavailableReason
                : result.Versions.Count == 0
                    ? "No committed versions of this object were found in the local copy."
                    : null;
        }
        catch (Exception ex)
        {
            HistoryVersions = [];
            HistoryMessage = $"The object's history could not be read — {ex.Message}";
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    partial void OnIsSplitViewChanged(bool value)
    {
        RaiseViewStateChanged();
        RefreshFind(); // the searchable rows switch between the new-side pane and the unified view
    }

    partial void OnIsLoadingChanged(bool value) => RaiseViewStateChanged();

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        RaiseViewStateChanged();
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        _ = RefreshChangesViewAsync(cts.Token);
    }

    private async Task RefreshChangesViewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FilterDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by further typing; the newest keystroke owns the refresh.
            return;
        }

        ChangesView.Refresh();
    }

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
        ViewedScriptText = null;
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

        // Viewing a historical version diffs that commit against its parent; the change type from
        // the run only applies to the run's own commit. Modified degrades gracefully when the
        // parent has no copy (first version) — the service returns an empty old side.
        var sha = SelectedVersion?.Sha ?? FullSha;
        var changeType = SelectedVersion is null ? change.ChangeType : ChangeType.Modified;

        IsLoading = true;
        try
        {
            var result = await _scriptHistory.GetVersionsAsync(
                _repository, sha, change.RelativePath, changeType, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsAvailable)
            {
                ErrorMessage = result.UnavailableReason;
                return;
            }

            // What "Copy script" copies: the script as shown — the old side only for deletions.
            ViewedScriptText = changeType == ChangeType.Deleted ? result.OldContent : result.NewContent;

            // Diffing thousands of lines is CPU work; keep it off the UI thread.
            switch (changeType)
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
                RefreshFind(); // the searchable rows just changed
            }
        }
    }

    [RelayCommand]
    private void CopySha() =>
        // Copies what is on screen: the viewed version's commit, or the run's.
        CopyToClipboard(SelectedVersion?.Sha ?? FullSha, "Commit SHA copied");

    [RelayCommand]
    private void CopyObjectName(ObjectChange? change) => CopyToClipboard(change?.QualifiedName, "Object name copied");

    [RelayCommand]
    private void CopyPath(ObjectChange? change) => CopyToClipboard(change?.RelativePath, "Path copied");

    private bool CanCopyScript() => !string.IsNullOrEmpty(ViewedScriptText);

    [RelayCommand(CanExecute = nameof(CanCopyScript))]
    private void CopyScript() => CopyToClipboard(ViewedScriptText, "Script copied");

    private void CopyToClipboard(string? text, string confirmation)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // The clipboard was briefly locked by another process; a copy is not worth crashing over.
            return;
        }

        _ = ShowCopyStatusAsync(confirmation);
    }

    // Confirmation lingers briefly, then clears — unless a newer copy has replaced it meanwhile.
    private async Task ShowCopyStatusAsync(string confirmation)
    {
        var version = ++_copyStatusVersion;
        CopyStatus = confirmation;
        await Task.Delay(CopyStatusDuration);
        if (version == _copyStatusVersion)
        {
            CopyStatus = null;
        }
    }

    // ------------------------------- Find within the viewed script ------------------------------

    /// <summary>The rows the find runs over: the new-side pane in split view, the single pane otherwise.</summary>
    private IReadOnlyList<DiffRow> SearchRows => ShowSplit ? NewRows : SingleRows;

    /// <summary>"3/17" while there are matches, "0/0" for a fruitless query, null with no query.</summary>
    public string? FindCounter => FindText.Length == 0
        ? null
        : _findMatches.Count == 0 ? "0/0" : $"{_findPosition + 1}/{_findMatches.Count}";

    partial void OnFindTextChanged(string value) => RefreshFind();

    /// <summary>Recomputes the matches (query or rows changed) and lands on the first one.</summary>
    private void RefreshFind()
    {
        _findMatches = DiffTextSearch.FindMatches(SearchRows, FindText);
        MoveFind(_findMatches.Count > 0 ? 0 : -1);
    }

    [RelayCommand]
    private void FindNext() => MoveFind(DiffTextSearch.NextPosition(_findMatches.Count, _findPosition));

    [RelayCommand]
    private void FindPrevious() => MoveFind(DiffTextSearch.PreviousPosition(_findMatches.Count, _findPosition));

    [RelayCommand]
    private void ClearFind() => FindText = string.Empty;

    private void MoveFind(int position)
    {
        _findPosition = position;
        FindCurrentRow = position >= 0 ? SearchRows[_findMatches[position]] : null;
        OnPropertyChanged(nameof(FindCounter));
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

    // The most specific page GitHub can show: the selected file at the VIEWED commit, else the commit.
    private string? BuildGitHubUrl()
    {
        if (_repository is { } repo && (SelectedVersion?.Sha ?? FullSha) is { } sha)
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

        if (TypeFilter is { } type && change.ChangeType != type)
        {
            return false;
        }

        var query = FilterText.Trim();
        return query.Length == 0
            || change.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || change.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
