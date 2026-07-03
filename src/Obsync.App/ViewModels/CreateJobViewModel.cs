using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Metadata;
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

/// <summary>A SQL object type that can be selected for the Custom object preset.</summary>
public sealed partial class SelectableObjectType(SqlObjectType type, string displayName) : ObservableObject
{
    public SqlObjectType Type { get; } = type;
    public string DisplayName { get; } = displayName;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>One labelled line on the Review step.</summary>
public sealed record ReviewItem(string Label, string Value);

/// <summary>
/// Drives the "Create / Edit Sync Job" wizard as a five-step stepper:
/// Source → Objects → Destination → Schedule → Review. Each step is validated before the user
/// can advance, and the final step shows a read-only summary before the job is saved.
/// </summary>
public sealed partial class CreateJobViewModel : ObservableObject
{
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IJobRepository _jobs;
    private readonly ISqlServerProbe _probe;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

    private SyncJob? _editingJob;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private bool _isEditMode;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SqlConnectionProfile? _selectedConnection;
    [ObservableProperty] private ObjectSelectionPreset _selectedPreset = ObjectSelectionPreset.Recommended;
    [ObservableProperty] private GitRepositoryProfile? _selectedRepository;
    [ObservableProperty] private string _branch = "main";
    [ObservableProperty] private string _destinationFolder = string.Empty;
    [ObservableProperty] private string? _localExportPath;
    [ObservableProperty] private CommitMode _selectedCommitMode = CommitMode.DirectCommit;
    [ObservableProperty] private string _reviewers = string.Empty;
    [ObservableProperty] private string _exportPath = string.Empty;

    [ObservableProperty] private ScheduleKind _selectedScheduleKind = ScheduleKind.Manual;
    [ObservableProperty] private int _intervalHours = 1;
    [ObservableProperty] private string _timeOfDay = "23:00";
    [ObservableProperty] private DayOfWeek _selectedDayOfWeek = DayOfWeek.Sunday;
    [ObservableProperty] private string _cronExpression = "0 0 23 * * ?";
    [ObservableProperty] private bool _runOnStartup;
    [ObservableProperty] private bool _runOnlyIfChanges = true;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<SqlConnectionProfile> Connections { get; } = [];
    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];
    public ObservableCollection<SelectableDatabase> Databases { get; } = [];
    public ObservableCollection<SelectableObjectType> ObjectTypes { get; } = [];
    public ObservableCollection<ReviewItem> ReviewItems { get; } = [];

    public IReadOnlyList<ObjectSelectionPreset> Presets { get; } = Enum.GetValues<ObjectSelectionPreset>();
    public IReadOnlyList<ScheduleKind> ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<CommitMode> CommitModes { get; } = Enum.GetValues<CommitMode>();

    /// <summary>True when the pull-request commit mode is selected (shows the reviewers field).</summary>
    public bool IsPullRequest => SelectedCommitMode == CommitMode.PullRequest;

    /// <summary>True for Export Only — shows the export-destination field and hides repository/branch.</summary>
    public bool IsExportOnly => SelectedCommitMode == CommitMode.ExportOnly;

    /// <summary>True for the git modes (Direct / PR / Local commit) — shows repository + branch.</summary>
    public bool IsGitMode => !IsExportOnly;

    partial void OnSelectedCommitModeChanged(CommitMode value)
    {
        OnPropertyChanged(nameof(IsPullRequest));
        OnPropertyChanged(nameof(IsExportOnly));
        OnPropertyChanged(nameof(IsGitMode));
    }

    public event EventHandler? Saved;

    public CreateJobViewModel(
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IJobRepository jobs,
        ISqlServerProbe probe,
        ICredentialStore credentialStore,
        IClock clock,
        IAuditWriter audit)
    {
        _connections = connections;
        _repositories = repositories;
        _jobs = jobs;
        _probe = probe;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;

        foreach (var descriptor in SqlObjectTypeCatalog.All)
        {
            ObjectTypes.Add(new SelectableObjectType(descriptor.Type, descriptor.DisplayName));
        }
    }

    // --- Step state surfaced to the view -------------------------------------------------------

    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;
    public bool IsFirstStep => CurrentStep == 1;
    public bool IsLastStep => CurrentStep == 5;
    public bool ShowBack => !IsFirstStep;
    public bool ShowNext => !IsLastStep;
    public string Title => IsEditMode ? "Edit Sync Job" : "Create Sync Job";

    public string StepCaption => CurrentStep switch
    {
        1 => "Step 1 of 5 · Source",
        2 => "Step 2 of 5 · Objects",
        3 => "Step 3 of 5 · Destination",
        4 => "Step 4 of 5 · Schedule",
        _ => "Step 5 of 5 · Review",
    };

    public bool IsCustomPreset => SelectedPreset == ObjectSelectionPreset.Custom;
    public bool ShowHourly => SelectedScheduleKind == ScheduleKind.Hourly;
    public bool ShowDayTime => SelectedScheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly;
    public bool ShowWeekday => SelectedScheduleKind == ScheduleKind.Weekly;
    public bool ShowCron => SelectedScheduleKind == ScheduleKind.Cron;

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        OnPropertyChanged(nameof(IsStep5));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(ShowBack));
        OnPropertyChanged(nameof(ShowNext));
        OnPropertyChanged(nameof(StepCaption));
    }

    partial void OnIsEditModeChanged(bool value) => OnPropertyChanged(nameof(Title));

    partial void OnSelectedPresetChanged(ObjectSelectionPreset value) => OnPropertyChanged(nameof(IsCustomPreset));

    partial void OnSelectedScheduleKindChanged(ScheduleKind value)
    {
        OnPropertyChanged(nameof(ShowHourly));
        OnPropertyChanged(nameof(ShowDayTime));
        OnPropertyChanged(nameof(ShowWeekday));
        OnPropertyChanged(nameof(ShowCron));
    }

    partial void OnSelectedRepositoryChanged(GitRepositoryProfile? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(Branch))
        {
            Branch = value.DefaultBranch;
        }
    }

    // --- Loading -------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        Connections.Clear();
        foreach (var connection in await _connections.GetAllAsync())
        {
            Connections.Add(connection);
        }

        Repositories.Clear();
        foreach (var repository in await _repositories.GetAllAsync())
        {
            Repositories.Add(repository);
        }
    }

    /// <summary>Populates the wizard from an existing job for editing. Call after <see cref="LoadAsync"/>.</summary>
    public void InitializeForEdit(SyncJob job)
    {
        IsEditMode = true;
        _editingJob = job;

        Name = job.Name;
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == job.ConnectionProfileId);
        SelectedRepository = Repositories.FirstOrDefault(r => r.Id == job.RepositoryProfileId);
        Branch = job.Branch ?? SelectedRepository?.DefaultBranch ?? "main";
        DestinationFolder = job.DestinationFolder;
        LocalExportPath = job.LocalExportPath;
        SelectedCommitMode = job.CommitMode;
        Reviewers = string.Join(", ", job.Reviewers);
        ExportPath = job.ExportPath ?? string.Empty;
        SelectedPreset = job.Selection.Preset;

        Databases.Clear();
        foreach (var database in job.Databases)
        {
            Databases.Add(new SelectableDatabase(database) { IsSelected = true });
        }

        foreach (var objectType in ObjectTypes)
        {
            objectType.IsSelected = job.Selection.CustomTypes.Contains(objectType.Type);
        }

        SelectedScheduleKind = job.Schedule.Kind;
        IntervalHours = job.Schedule.IntervalHours;
        TimeOfDay = job.Schedule.TimeOfDay.ToString("HH:mm");
        SelectedDayOfWeek = job.Schedule.DayOfWeek;
        CronExpression = job.Schedule.CronExpression ?? "0 0 23 * * ?";
        RunOnStartup = job.Schedule.RunOnStartup;
        RunOnlyIfChanges = job.Schedule.RunOnlyIfChanges;
    }

    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        if (SelectedConnection is null)
        {
            StatusMessage = "Select a server first.";
            return;
        }

        if (IsBusy)
        {
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
            if (result.IsFailure)
            {
                StatusMessage = result.Error;
                return;
            }

            // Preserve any already-selected databases (e.g. when editing) across a refresh.
            var selected = Databases.Where(d => d.IsSelected).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Databases.Clear();
            foreach (var database in result.Value)
            {
                Databases.Add(new SelectableDatabase(database.Name) { IsSelected = selected.Contains(database.Name) });
            }

            StatusMessage = $"Found {result.Value.Count} database(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load databases — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Stepper navigation --------------------------------------------------------------------

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 1)
        {
            CurrentStep--;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    private void Next()
    {
        var error = ValidateStep(CurrentStep);
        if (error is not null)
        {
            StatusMessage = error;
            return;
        }

        StatusMessage = null;
        if (CurrentStep < 5)
        {
            CurrentStep++;
            if (CurrentStep == 5)
            {
                BuildReview();
            }
        }
    }

    private string? ValidateStep(int step) => step switch
    {
        1 when string.IsNullOrWhiteSpace(Name) => "Enter a job name.",
        1 when SelectedConnection is null => "Select a server.",
        1 when !Databases.Any(d => d.IsSelected) => "Select at least one database.",
        2 when IsCustomPreset && !ObjectTypes.Any(t => t.IsSelected) => "Select at least one object type for the Custom preset.",
        3 when IsExportOnly && string.IsNullOrWhiteSpace(ExportPath) => "Enter an export destination (a folder or .zip path).",
        3 when IsGitMode && SelectedRepository is null => "Select a destination repository.",
        3 when IsGitMode && string.IsNullOrWhiteSpace(Branch) => "Enter a branch.",
        4 when SelectedScheduleKind == ScheduleKind.Cron && string.IsNullOrWhiteSpace(CronExpression) => "Enter a cron expression.",
        _ => null,
    };

    private void BuildReview()
    {
        var databases = Databases.Where(d => d.IsSelected).Select(d => d.Name).ToList();
        var objects = IsCustomPreset
            ? string.Join(", ", ObjectTypes.Where(t => t.IsSelected).Select(t => t.DisplayName))
            : $"{SelectedPreset} preset";

        ReviewItems.Clear();
        ReviewItems.Add(new ReviewItem("Job name", Name.Trim()));
        ReviewItems.Add(new ReviewItem("Source", SelectedConnection is null
            ? "—"
            : $"{SelectedConnection.Name} ({SelectedConnection.ServerName}) · {SelectedConnection.AuthenticationMode}"));
        ReviewItems.Add(new ReviewItem("Databases", databases.Count == 0 ? "—" : string.Join(", ", databases)));
        ReviewItems.Add(new ReviewItem("Objects", objects));
        if (IsExportOnly)
        {
            ReviewItems.Add(new ReviewItem("Export to", string.IsNullOrWhiteSpace(ExportPath) ? "—" : ExportPath.Trim()));
        }
        else
        {
            ReviewItems.Add(new ReviewItem("Repository", SelectedRepository?.FullName ?? "—"));
            ReviewItems.Add(new ReviewItem("Branch", Branch));
        }

        ReviewItems.Add(new ReviewItem("Folder", EffectiveDestinationFolder(databases)));
        ReviewItems.Add(new ReviewItem("Schedule", BuildSchedule().Describe()));
        ReviewItems.Add(new ReviewItem("On changes", RunOnlyIfChanges ? "Commit only when changes are detected" : "Always create a run"));
        ReviewItems.Add(new ReviewItem("Commit mode", SelectedCommitMode switch
        {
            CommitMode.PullRequest => $"Pull request → {Branch}",
            CommitMode.LocalCommitOnly => "Local commit (not pushed)",
            CommitMode.ExportOnly => "Export only (no GitHub)",
            _ => "Direct commit & push",
        }));
        if (SelectedCommitMode == CommitMode.PullRequest && ParseReviewers(Reviewers) is { Count: > 0 } reviewers)
        {
            ReviewItems.Add(new ReviewItem("Reviewers", string.Join(", ", reviewers)));
        }
        ReviewItems.Add(new ReviewItem("Security",
            "SQL password and GitHub token are read from Windows Credential Manager; never written to disk or logs."));
    }

    // Parse the reviewers textbox: split on commas/whitespace/semicolons, strip a leading '@',
    // drop blanks, de-dupe case-insensitively.
    private static List<string> ParseReviewers(string raw) =>
        [.. raw.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.TrimStart('@'))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private string EffectiveDestinationFolder(IReadOnlyList<string> databases) =>
        string.IsNullOrWhiteSpace(DestinationFolder)
            ? $"environments/{SelectedConnection?.ServerName}/{(databases.Count > 0 ? databases[0] : "db")}"
            : DestinationFolder.Trim();

    private ScheduleProfile BuildSchedule()
    {
        var time = TimeOnly.TryParseExact(TimeOfDay, "HH:mm", out var parsed) ? parsed : new TimeOnly(23, 0);
        return new ScheduleProfile
        {
            Kind = SelectedScheduleKind,
            IntervalHours = Math.Max(1, IntervalHours),
            TimeOfDay = time,
            DayOfWeek = SelectedDayOfWeek,
            CronExpression = CronExpression,
            RunOnStartup = RunOnStartup,
            RunOnlyIfChanges = RunOnlyIfChanges,
        };
    }

    // --- Save ----------------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        for (var step = 1; step <= 4; step++)
        {
            var error = ValidateStep(step);
            if (error is not null)
            {
                CurrentStep = step;
                StatusMessage = error;
                return;
            }
        }

        var selectedDatabases = Databases.Where(d => d.IsSelected).Select(d => d.Name).ToList();

        // When editing, mutate the existing job so fields the wizard does not surface
        // (Description, Enabled, Advanced options, RunSummary, and the non-preset Selection settings)
        // are preserved instead of being reset to defaults by the upsert.
        var job = IsEditMode && _editingJob is not null ? _editingJob : new SyncJob();
        job.Name = Name.Trim();
        job.ConnectionProfileId = SelectedConnection!.Id;
        // Export Only has no GitHub repository/branch; the git modes require one (validated above).
        job.RepositoryProfileId = IsExportOnly ? null : SelectedRepository!.Id;
        job.Branch = IsExportOnly ? null : (string.IsNullOrWhiteSpace(Branch) ? SelectedRepository!.DefaultBranch : Branch.Trim());
        job.ExportPath = IsExportOnly ? ExportPath.Trim() : null;
        job.Databases = selectedDatabases;
        job.DestinationFolder = EffectiveDestinationFolder(selectedDatabases);
        job.LocalExportPath = string.IsNullOrWhiteSpace(LocalExportPath) ? null : LocalExportPath.Trim();
        job.CommitMode = SelectedCommitMode;
        job.Reviewers = SelectedCommitMode == CommitMode.PullRequest ? ParseReviewers(Reviewers) : [];
        job.Selection.Preset = SelectedPreset;
        if (IsCustomPreset)
        {
            job.Selection.CustomTypes = [.. ObjectTypes.Where(t => t.IsSelected).Select(t => t.Type)];
        }

        job.Schedule = BuildSchedule();
        job.UpdatedAt = _clock.UtcNow;
        if (!IsEditMode)
        {
            job.CreatedAt = _clock.UtcNow;
        }

        IsBusy = true;
        try
        {
            // The desktop app is the source of truth (SQLite); the Obsync Windows Service is the
            // scheduler host and picks up jobs and their schedules when it loads them. The app runs
            // jobs on demand via Run Now.
            await _jobs.UpsertAsync(job);
            await _audit.WriteAsync(
                IsEditMode ? AuditAction.JobEdited : AuditAction.JobCreated, "Job", job.Id.ToString(), job.Name);
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save the job — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
