using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Scheduler;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.App.ViewModels;

/// <summary>A database that can be selected for inclusion in a job.</summary>
public sealed partial class SelectableDatabase(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Drives the "Create Sync Job" flow: source → objects → destination → schedule → review.</summary>
public sealed partial class CreateJobViewModel : ObservableObject
{
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IJobRepository _jobs;
    private readonly ISqlServerProbe _probe;
    private readonly ICredentialStore _credentialStore;
    private readonly ISyncJobScheduler _scheduler;
    private readonly IClock _clock;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SqlConnectionProfile? _selectedConnection;
    [ObservableProperty] private ObjectSelectionPreset _selectedPreset = ObjectSelectionPreset.Recommended;
    [ObservableProperty] private GitRepositoryProfile? _selectedRepository;
    [ObservableProperty] private string _branch = "main";
    [ObservableProperty] private string _destinationFolder = string.Empty;
    [ObservableProperty] private ScheduleKind _selectedScheduleKind = ScheduleKind.Manual;
    [ObservableProperty] private string _cronExpression = "0 0 23 * * ?";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<SqlConnectionProfile> Connections { get; } = [];
    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];
    public ObservableCollection<SelectableDatabase> Databases { get; } = [];

    public IReadOnlyList<ObjectSelectionPreset> Presets { get; } = Enum.GetValues<ObjectSelectionPreset>();
    public IReadOnlyList<ScheduleKind> ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();

    public event EventHandler? Saved;

    public CreateJobViewModel(
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IJobRepository jobs,
        ISqlServerProbe probe,
        ICredentialStore credentialStore,
        ISyncJobScheduler scheduler,
        IClock clock)
    {
        _connections = connections;
        _repositories = repositories;
        _jobs = jobs;
        _probe = probe;
        _credentialStore = credentialStore;
        _scheduler = scheduler;
        _clock = clock;
    }

    public async Task LoadAsync()
    {
        foreach (var connection in await _connections.GetAllAsync())
        {
            Connections.Add(connection);
        }

        foreach (var repository in await _repositories.GetAllAsync())
        {
            Repositories.Add(repository);
        }
    }

    partial void OnSelectedRepositoryChanged(GitRepositoryProfile? value)
    {
        if (value is not null)
        {
            Branch = value.DefaultBranch;
        }
    }

    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        if (SelectedConnection is null)
        {
            StatusMessage = "Select a connection first.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Loading databases…";
        try
        {
            var password = SelectedConnection.RequiresPassword
                ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(SelectedConnection.Id))
                : null;
            var result = await _probe.GetDatabasesAsync(SelectedConnection, password);
            Databases.Clear();
            if (result.IsFailure)
            {
                StatusMessage = result.Error;
                return;
            }

            foreach (var database in result.Value)
            {
                Databases.Add(new SelectableDatabase(database.Name));
            }

            StatusMessage = $"Found {result.Value.Count} database(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var selectedDatabases = Databases.Where(d => d.IsSelected).Select(d => d.Name).ToList();
        if (string.IsNullOrWhiteSpace(Name) || SelectedConnection is null || SelectedRepository is null || selectedDatabases.Count == 0)
        {
            StatusMessage = "Provide a name, a connection, at least one database, and a repository.";
            return;
        }

        var job = new SyncJob
        {
            Name = Name.Trim(),
            ConnectionProfileId = SelectedConnection.Id,
            RepositoryProfileId = SelectedRepository.Id,
            Databases = selectedDatabases,
            Branch = string.IsNullOrWhiteSpace(Branch) ? SelectedRepository.DefaultBranch : Branch.Trim(),
            DestinationFolder = string.IsNullOrWhiteSpace(DestinationFolder)
                ? $"environments/{SelectedConnection.ServerName}/{selectedDatabases[0]}"
                : DestinationFolder.Trim(),
            Selection = new ObjectSelectionProfile { Preset = SelectedPreset },
            Schedule = new ScheduleProfile { Kind = SelectedScheduleKind, CronExpression = CronExpression },
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };

        await _jobs.UpsertAsync(job);
        await _scheduler.ScheduleJobAsync(job);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
