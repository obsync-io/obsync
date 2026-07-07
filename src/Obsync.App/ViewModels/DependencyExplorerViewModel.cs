using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The Job Workspace's Dependencies tab: pick an object from the job's locally indexed catalog
/// (instant, works offline), then read its one-level dependency graph from the live server —
/// what would be affected by a change, and what the object itself references. Read-only.
/// </summary>
public sealed partial class DependencyExplorerViewModel : ObservableObject
{
    /// <summary>How many picker matches the search shows; typing narrows the rest away.</summary>
    private const int MaxSearchResults = 50;

    private readonly IObjectStateRepository _objectStates;
    private readonly ISqlServerProbe _probe;
    private readonly ICredentialStore _credentialStore;

    private SyncJob? _job;
    private SqlConnectionProfile? _connection;
    private CancellationTokenSource? _searchDebounce;

    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private TrackedObjectState? _selectedObject;
    [ObservableProperty] private bool _isLoading;

    /// <summary>The analyzed object's display header, e.g. "dbo.Customers — Table".</summary>
    [ObservableProperty] private string? _currentObjectLabel;

    /// <summary>Status or error line under the results (lookup failures, empty-state hints).</summary>
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty] private string? _usedBySummary;
    [ObservableProperty] private string? _usesSummary;

    /// <summary>True once a lookup has produced results (drives the results panel visibility).</summary>
    [ObservableProperty] private bool _hasResults;

    public ObservableCollection<string> Databases { get; } = [];
    public ObservableCollection<TrackedObjectState> SearchResults { get; } = [];
    public ObservableCollection<SqlDependencyItem> UsedBy { get; } = [];
    public ObservableCollection<SqlDependencyItem> Uses { get; } = [];

    public DependencyExplorerViewModel(
        IObjectStateRepository objectStates, ISqlServerProbe probe, ICredentialStore credentialStore)
    {
        _objectStates = objectStates;
        _probe = probe;
        _credentialStore = credentialStore;
    }

    /// <summary>Called by the Job Workspace when its job (and connection) load or reload.</summary>
    public async Task InitializeAsync(SyncJob job, SqlConnectionProfile? connection)
    {
        _job = job;
        _connection = connection;

        var databases = await _objectStates.GetDatabasesForJobAsync(job.Id);
        Databases.Clear();
        foreach (var database in databases)
        {
            Databases.Add(database);
        }

        StatusMessage = Databases.Count == 0
            ? "Run this job once to index its objects — the explorer works from the last synced catalog."
            : null;

        // Keep the current database when it still exists so a reload doesn't lose the user's place.
        SelectedDatabase = SelectedDatabase is { } current && Databases.Contains(current)
            ? current
            : Databases.FirstOrDefault();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        SearchText = string.Empty;
        SelectedObject = null;
        ClearResults();
        _ = SearchNowAsync();
    }

    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync();

    partial void OnSelectedObjectChanged(TrackedObjectState? value)
    {
        if (value is not null)
        {
            _ = AnalyzeAsync(value.SchemaName, value.ObjectName, value.ObjectType.ToString());
        }
    }

    private async Task DebouncedSearchAsync()
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            await SearchNowAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer keystrokes.
        }
    }

    /// <summary>Runs the picker search immediately (the debounce wrapper calls this).</summary>
    internal async Task SearchNowAsync(CancellationToken cancellationToken = default)
    {
        if (_job is null || SelectedDatabase is not { } database)
        {
            SearchResults.Clear();
            return;
        }

        var results = await _objectStates.SearchAsync(_job.Id, database, SearchText, MaxSearchResults, cancellationToken);
        SearchResults.Clear();
        foreach (var result in results)
        {
            SearchResults.Add(result);
        }
    }

    /// <summary>Drills into a dependency: the clicked object becomes the analyzed one.</summary>
    [RelayCommand]
    private Task DrillIntoAsync(SqlDependencyItem? item) =>
        item is { IsDrillable: true } ? AnalyzeAsync(item.Schema, item.Name, item.TypeLabel) : Task.CompletedTask;

    private async Task AnalyzeAsync(string schema, string name, string typeLabel)
    {
        if (_job is null || _connection is null || SelectedDatabase is not { } database || IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        CurrentObjectLabel = $"{schema}.{name}";
        try
        {
            var password = _connection.RequiresPassword
                ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(_connection.Id))
                : null;
            var result = await _probe.GetDependenciesAsync(_connection, password, database, schema, name);
            if (result.IsFailure)
            {
                ClearResults();
                StatusMessage = result.Error;
                return;
            }

            UsedBy.Clear();
            foreach (var item in result.Value.UsedBy)
            {
                UsedBy.Add(item);
            }

            Uses.Clear();
            foreach (var item in result.Value.Uses)
            {
                Uses.Add(item);
            }

            UsedBySummary = Summarize(result.Value.UsedBy);
            UsesSummary = Summarize(result.Value.Uses);
            HasResults = true;
        }
        catch (Exception ex)
        {
            ClearResults();
            StatusMessage = $"Dependency lookup failed — {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearResults()
    {
        UsedBy.Clear();
        Uses.Clear();
        UsedBySummary = null;
        UsesSummary = null;
        HasResults = false;
        CurrentObjectLabel = null;
    }

    /// <summary>"3 views · 2 stored procedures · 1 trigger" — the pitch line for one direction.</summary>
    internal static string Summarize(IReadOnlyList<SqlDependencyItem> items)
    {
        if (items.Count == 0)
        {
            return "none";
        }

        return string.Join(" · ", items
            .GroupBy(i => i.TypeLabel)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} {Pluralize(g.Key.ToLowerInvariant(), g.Count())}"));
    }

    private static string Pluralize(string label, int count) => count == 1 ? label : label switch
    {
        // "table (foreign key)" pluralizes inside the parenthetical's head noun.
        "table (foreign key)" => "tables (foreign key)",
        _ => label + "s",
    };
}
