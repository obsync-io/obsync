using System.Collections.ObjectModel;
using System.IO;
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

/// <summary>A table that can be picked for reference-data versioning.</summary>
public sealed partial class SelectableTable(string qualifiedName, long? rowCount = null) : ObservableObject
{
    public string QualifiedName { get; } = qualifiedName;

    /// <summary>Approximate row count, when the list came from the server (null for saved entries).</summary>
    public long? RowCount { get; } = rowCount;

    public string Display => RowCount is { } rows ? $"{QualifiedName}   ·   {rows:N0} rows" : QualifiedName;

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
    private readonly Services.ISchedulerHealthService _schedulerHealth;

    private SyncJob? _editingJob;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private bool _isEditMode;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private SqlConnectionProfile? _selectedConnection;
    [ObservableProperty] private bool _syncAllUserDatabases;
    [ObservableProperty] private ObjectSelectionPreset _selectedPreset = ObjectSelectionPreset.Recommended;
    [ObservableProperty] private bool _includeServerObjects;
    [ObservableProperty] private bool _includeReferenceData;

    // The per-database generated files (all default on; see ObjectSelectionProfile).
    [ObservableProperty] private bool _includeObjectInventory = true;
    [ObservableProperty] private bool _includeDatabaseOptions = true;
    [ObservableProperty] private bool _includePermissionsFile = true;
    [ObservableProperty] private bool _includeDocumentation = true;
    [ObservableProperty] private bool _includeSecurityReview = true;
    [ObservableProperty] private string _tableSourceDatabase = string.Empty;
    [ObservableProperty] private int _referenceDataMaxRows = 5000;
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

    [ObservableProperty] private bool _maintenanceWindowEnabled;
    [ObservableProperty] private string _windowStart = "22:00";
    [ObservableProperty] private string _windowEnd = "05:00";
    [ObservableProperty] private MaintenanceDayScope _selectedDayScope = MaintenanceDayScope.AnyDay;

    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private int _maxParallelWorkers;
    [ObservableProperty] private int _queryTimeoutSeconds = 120;
    [ObservableProperty] private int _lockTimeoutSeconds;
    [ObservableProperty] private bool _incrementalScripting = true;

    /// <summary>Comma-separated schema allow-list; empty = all schemas.</summary>
    [ObservableProperty] private string _schemaFilter = string.Empty;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Shown on the Schedule step when the background service can't execute schedules right now, so
    /// the user never leaves the wizard believing a schedule is live when it isn't. Informational —
    /// the schedule is still saved and activates once the service is healthy.
    /// </summary>
    [ObservableProperty] private string? _scheduleServiceNotice;

    public ObservableCollection<SqlConnectionProfile> Connections { get; } = [];
    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];
    public ObservableCollection<SelectableDatabase> Databases { get; } = [];
    public ObservableCollection<SelectableObjectType> ObjectTypes { get; } = [];
    public ObservableCollection<SelectableObjectType> ServerObjectTypes { get; } = [];
    public ObservableCollection<SelectableTable> ReferenceTables { get; } = [];
    public ObservableCollection<ReviewItem> ReviewItems { get; } = [];

    public IReadOnlyList<ObjectSelectionPreset> Presets { get; } = Enum.GetValues<ObjectSelectionPreset>();
    public IReadOnlyList<ScheduleKind> ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<CommitMode> CommitModes { get; } = Enum.GetValues<CommitMode>();
    public IReadOnlyList<MaintenanceDayScope> DayScopes { get; } = Enum.GetValues<MaintenanceDayScope>();

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
        IAuditWriter audit,
        Services.ISchedulerHealthService schedulerHealth)
    {
        _connections = connections;
        _repositories = repositories;
        _jobs = jobs;
        _probe = probe;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;
        _schedulerHealth = schedulerHealth;

        // The main picker offers database objects; server-scoped types have their own checklist.
        foreach (var descriptor in SqlObjectTypeCatalog.All)
        {
            (descriptor.IsServerScoped ? ServerObjectTypes : ObjectTypes)
                .Add(new SelectableObjectType(descriptor.Type, descriptor.DisplayName));
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

    /// <summary>Caption over the database checklist — its meaning flips with the scope.</summary>
    public string DatabasesHeader => SyncAllUserDatabases ? "EXCLUDE DATABASES (OPTIONAL)" : "DATABASES";

    /// <summary>One-line explanation of what checking a database does in the current scope.</summary>
    public string DatabasesHint => SyncAllUserDatabases
        ? "Every online user database is scripted on each run — databases created later are picked up automatically. Check any you want left out."
        : "Check the databases to include in this job.";

    partial void OnSyncAllUserDatabasesChanged(bool value)
    {
        OnPropertyChanged(nameof(DatabasesHeader));
        OnPropertyChanged(nameof(DatabasesHint));
    }

    partial void OnSelectedPresetChanged(ObjectSelectionPreset value) => OnPropertyChanged(nameof(IsCustomPreset));

    partial void OnIncludeServerObjectsChanged(bool value)
    {
        // First switch-on with nothing picked yet: default to every server type.
        if (value && !ServerObjectTypes.Any(t => t.IsSelected))
        {
            foreach (var type in ServerObjectTypes)
            {
                type.IsSelected = true;
            }
        }
    }

    partial void OnIncludeReferenceDataChanged(bool value)
    {
        // Default the table-source picker to the first relevant database from the Source step.
        if (value && TableSourceDatabase.Length == 0)
        {
            TableSourceDatabase = Databases.FirstOrDefault(d => d.IsSelected)?.Name
                ?? Databases.FirstOrDefault()?.Name ?? string.Empty;
        }
    }

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

        var health = await _schedulerHealth.GetAsync();
        ScheduleServiceNotice = health is { CanExecuteSchedules: false }
            ? $"{health.Summary} The schedule is saved either way and starts working once the service is running."
            : null;
    }

    /// <summary>Populates the wizard from an existing job for editing. Call after <see cref="LoadAsync"/>.</summary>
    public void InitializeForEdit(SyncJob job)
    {
        IsEditMode = true;
        _editingJob = job;

        Name = job.Name;
        Tags = string.Join(", ", job.Tags);
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == job.ConnectionProfileId);
        SelectedRepository = Repositories.FirstOrDefault(r => r.Id == job.RepositoryProfileId);
        Branch = job.Branch ?? SelectedRepository?.DefaultBranch ?? "main";
        DestinationFolder = job.DestinationFolder;
        LocalExportPath = job.LocalExportPath;
        SelectedCommitMode = job.CommitMode;
        Reviewers = string.Join(", ", job.Reviewers);
        ExportPath = job.ExportPath ?? string.Empty;
        SelectedPreset = job.Selection.Preset;
        SchemaFilter = string.Join(", ", job.Selection.SchemaFilter);

        // In the dynamic scope the checklist holds the EXCLUSIONS; otherwise the selected list.
        SyncAllUserDatabases = job.DatabaseScope == DatabaseScope.AllUserDatabases;
        Databases.Clear();
        foreach (var database in SyncAllUserDatabases ? job.ExcludedDatabases : job.Databases)
        {
            Databases.Add(new SelectableDatabase(database) { IsSelected = true });
        }

        foreach (var objectType in ObjectTypes)
        {
            objectType.IsSelected = job.Selection.CustomTypes.Contains(objectType.Type);
        }

        // Restore the checks before the toggle so switching it on keeps the saved subset intact.
        foreach (var serverType in ServerObjectTypes)
        {
            serverType.IsSelected = job.Selection.ServerTypes.Contains(serverType.Type);
        }

        IncludeServerObjects = job.Selection.ServerTypes.Count > 0;

        SelectedScheduleKind = job.Schedule.Kind;
        IntervalHours = job.Schedule.IntervalHours;
        TimeOfDay = job.Schedule.TimeOfDay.ToString("HH:mm");
        SelectedDayOfWeek = job.Schedule.DayOfWeek;
        CronExpression = job.Schedule.CronExpression ?? "0 0 23 * * ?";
        RunOnStartup = job.Schedule.RunOnStartup;
        RunOnlyIfChanges = job.Schedule.RunOnlyIfChanges;

        MaintenanceWindowEnabled = job.Schedule.MaintenanceWindowEnabled;
        WindowStart = job.Schedule.WindowStart.ToString("HH:mm");
        WindowEnd = job.Schedule.WindowEnd.ToString("HH:mm");
        SelectedDayScope = job.Schedule.DayScope;

        IncludeObjectInventory = job.Selection.IncludeObjectInventory;
        IncludeDatabaseOptions = job.Selection.IncludeDatabaseOptions;
        IncludePermissionsFile = job.Selection.IncludeDatabasePermissionsFile;
        IncludeDocumentation = job.Selection.IncludeDocumentation;
        IncludeSecurityReview = job.Selection.IncludeSecurityReview;

        IncludeReferenceData = job.Selection.ReferenceDataTables.Count > 0;
        ReferenceTables.Clear();
        foreach (var tableName in job.Selection.ReferenceDataTables)
        {
            ReferenceTables.Add(new SelectableTable(tableName) { IsSelected = true });
        }

        ReferenceDataMaxRows = job.Advanced.ReferenceDataMaxRows;

        MaxParallelWorkers = job.Advanced.MaxParallelWorkers;
        QueryTimeoutSeconds = job.Advanced.SqlCommandTimeoutSeconds;
        LockTimeoutSeconds = job.Advanced.SqlLockTimeoutSeconds;
        IncrementalScripting = job.Advanced.IncrementalScripting;
        ShowAdvanced = job.Advanced.MaxParallelWorkers != 0 || job.Advanced.SqlLockTimeoutSeconds != 0
            || job.Advanced.SqlCommandTimeoutSeconds != 120 || !job.Advanced.IncrementalScripting;
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

    [RelayCommand]
    private async Task LoadTablesAsync()
    {
        if (SelectedConnection is null)
        {
            StatusMessage = "Select a server on the Source step first.";
            return;
        }

        var database = TableSourceDatabase.Trim();
        if (database.Length == 0)
        {
            StatusMessage = "Pick a database to list tables from.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Loading tables…";
        try
        {
            var password = SelectedConnection.RequiresPassword
                ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(SelectedConnection.Id))
                : null;
            var result = await _probe.GetTablesAsync(SelectedConnection, password, database);
            if (result.IsFailure)
            {
                StatusMessage = result.Error;
                return;
            }

            // Preserve checked tables (e.g. saved entries when editing) across a refresh, and keep
            // any checked entry the loaded database doesn't have — it may exist in another database.
            var selected = ReferenceTables.Where(t => t.IsSelected).Select(t => t.QualifiedName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ReferenceTables.Clear();
            foreach (var tableInfo in result.Value)
            {
                ReferenceTables.Add(new SelectableTable(tableInfo.QualifiedName, tableInfo.RowCount)
                {
                    IsSelected = selected.Remove(tableInfo.QualifiedName),
                });
            }

            foreach (var leftover in selected)
            {
                ReferenceTables.Add(new SelectableTable(leftover) { IsSelected = true });
            }

            StatusMessage = $"Found {result.Value.Count} table(s) in {database}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load tables — {ex.Message}";
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
        1 when !SyncAllUserDatabases && !Databases.Any(d => d.IsSelected) => "Select at least one database.",
        2 when IsCustomPreset && !ObjectTypes.Any(t => t.IsSelected) => "Select at least one object type for the Custom preset.",
        2 when IncludeServerObjects && !ServerObjectTypes.Any(t => t.IsSelected) =>
            "Select at least one server object type, or turn server-level objects off.",
        2 when IncludeReferenceData && !ReferenceTables.Any(t => t.IsSelected) =>
            "Select at least one reference table, or turn reference data off.",
        2 when IncludeReferenceData && ReferenceDataMaxRows < 1 => "The reference-data row cap must be at least 1.",
        3 when IsExportOnly && string.IsNullOrWhiteSpace(ExportPath) => "Enter an export destination (a folder or .zip path).",
        3 when IsExportOnly && (HasInvalidPathChars(ExportPath) || !Path.IsPathRooted(ExportPath.Trim())) =>
            "The export destination must be a full path (e.g. D:\\exports or \\\\server\\share\\db.zip) without invalid characters.",
        3 when IsGitMode && SelectedRepository is null => "Select a destination repository.",
        3 when IsGitMode && string.IsNullOrWhiteSpace(Branch) => "Enter a branch.",
        3 when !string.IsNullOrWhiteSpace(DestinationFolder)
            && (HasInvalidPathChars(DestinationFolder) || Path.IsPathRooted(DestinationFolder.Trim()) || HasParentSegment(DestinationFolder)) =>
            "The repository folder must be a relative path inside the repository (no drive letter, no '..', no invalid characters).",
        3 when !string.IsNullOrWhiteSpace(LocalExportPath)
            && (HasInvalidPathChars(LocalExportPath) || !Path.IsPathRooted(LocalExportPath.Trim())) =>
            "The local export path must be a full path (e.g. D:\\exports) without invalid characters.",
        4 when SelectedScheduleKind == ScheduleKind.Cron && string.IsNullOrWhiteSpace(CronExpression) => "Enter a cron expression.",
        4 when SelectedScheduleKind == ScheduleKind.Cron && !Quartz.CronExpression.IsValidExpression(CronExpression.Trim()) =>
            "The cron expression is not valid Quartz syntax (seconds minutes hours day-of-month month day-of-week [year]).",
        4 when SelectedScheduleKind == ScheduleKind.Cron && CronNeverFires(CronExpression.Trim()) =>
            "The cron expression never matches a future time, so the job would never run. Check the day/month/year fields.",
        4 when SelectedScheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly
            && !TimeOnly.TryParseExact(TimeOfDay, "HH:mm", out _) => "Enter a valid time of day (HH:mm).",
        4 when MaintenanceWindowEnabled && !TimeOnly.TryParseExact(WindowStart, "HH:mm", out _) => "Enter a valid window start time (HH:mm).",
        4 when MaintenanceWindowEnabled && !TimeOnly.TryParseExact(WindowEnd, "HH:mm", out _) => "Enter a valid window end time (HH:mm).",
        4 when ValidateScheduleAgainstWindow() is { } windowError => windowError,
        _ => null,
    };

    private static bool HasInvalidPathChars(string path) => path.IndexOfAny(Path.GetInvalidPathChars()) >= 0;

    // '..' segments would let a repo-relative destination escape the repository working tree.
    private static bool HasParentSegment(string path) => path.Split('/', '\\').Any(segment => segment.Trim() == "..");

    // Quartz throws for a trigger that never fires (e.g. Feb 31, a past year), which would take the
    // scheduler down — reject the expression here instead.
    private static bool CronNeverFires(string cron) =>
        new Quartz.CronExpression(cron) { TimeZone = TimeZoneInfo.Local }.GetNextValidTimeAfter(DateTimeOffset.Now) is null;

    /// <summary>
    /// A Daily/Weekly fire time that never lands inside an enabled maintenance window means the engine
    /// skips every scheduled run and the job silently never runs — reject the combination up front.
    /// </summary>
    private string? ValidateScheduleAgainstWindow()
    {
        if (!MaintenanceWindowEnabled || SelectedScheduleKind is not (ScheduleKind.Daily or ScheduleKind.Weekly))
        {
            return null;
        }

        var schedule = BuildSchedule();
        // Probe one calendar week (2024-01-01 is a Monday) so overnight windows attribute the fire to
        // the day the window opened, exactly as the engine's window check does at run time.
        var monday = new DateTime(2024, 1, 1);
        bool FiresOn(DayOfWeek day) => schedule.IsWithinMaintenanceWindow(new DateTimeOffset(
            monday.AddDays(((int)day - (int)DayOfWeek.Monday + 7) % 7) + schedule.TimeOfDay.ToTimeSpan(), TimeSpan.Zero));

        var days = SelectedDayScope switch
        {
            MaintenanceDayScope.WeekdaysOnly => ", weekdays",
            MaintenanceDayScope.WeekendsOnly => ", weekends",
            _ => string.Empty,
        };

        if (SelectedScheduleKind == ScheduleKind.Weekly)
        {
            return FiresOn(SelectedDayOfWeek)
                ? null
                : $"Weekly on {SelectedDayOfWeek} at {schedule.TimeOfDay:HH:mm} never falls inside the maintenance window " +
                  $"({schedule.WindowStart:HH:mm}–{schedule.WindowEnd:HH:mm}{days}), so the job would never run. Change the schedule or the window.";
        }

        return Enum.GetValues<DayOfWeek>().Any(FiresOn)
            ? null
            : $"Daily at {schedule.TimeOfDay:HH:mm} never falls inside the maintenance window " +
              $"({schedule.WindowStart:HH:mm}–{schedule.WindowEnd:HH:mm}), so the job would never run. Change the time or the window.";
    }

    private void BuildReview()
    {
        var databases = Databases.Where(d => d.IsSelected).Select(d => d.Name).ToList();
        var objects = IsCustomPreset
            ? string.Join(", ", ObjectTypes.Where(t => t.IsSelected).Select(t => t.DisplayName))
            : $"{SelectedPreset} preset";

        ReviewItems.Clear();
        ReviewItems.Add(new ReviewItem("Job name", Name.Trim()));
        if (JobTags.Parse(Tags) is { Count: > 0 } tags)
        {
            ReviewItems.Add(new ReviewItem("Tags", string.Join(", ", tags)));
        }
        ReviewItems.Add(new ReviewItem("Source", SelectedConnection is null
            ? "—"
            : $"{SelectedConnection.Name} ({SelectedConnection.ServerName}) · {SelectedConnection.AuthenticationMode}"));
        ReviewItems.Add(new ReviewItem("Databases", SyncAllUserDatabases
            ? databases.Count == 0 ? "All user databases" : $"All user databases, excluding {string.Join(", ", databases)}"
            : databases.Count == 0 ? "—" : string.Join(", ", databases)));
        ReviewItems.Add(new ReviewItem("Objects", objects));
        if (ParseSchemaFilter() is { Count: > 0 } schemas)
        {
            ReviewItems.Add(new ReviewItem("Schemas", string.Join(", ", schemas)));
        }
        if (IncludeServerObjects && ServerObjectTypes.Any(t => t.IsSelected))
        {
            ReviewItems.Add(new ReviewItem("Server objects",
                string.Join(", ", ServerObjectTypes.Where(t => t.IsSelected).Select(t => t.DisplayName))));
        }
        if (IncludeReferenceData && ReferenceTables.Any(t => t.IsSelected))
        {
            var tables = ReferenceTables.Where(t => t.IsSelected).Select(t => t.QualifiedName).ToList();
            ReviewItems.Add(new ReviewItem("Reference data",
                $"{string.Join(", ", tables)} · cap {ReferenceDataMaxRows:N0} rows/table"));
        }
        var generatedFiles = new[]
        {
            (IncludeObjectInventory, "inventory"),
            (IncludeDatabaseOptions, "database options"),
            (IncludePermissionsFile, "permissions"),
            (IncludeDocumentation, "documentation"),
            (IncludeSecurityReview, "security review"),
        }.Where(f => f.Item1).Select(f => f.Item2).ToList();
        ReviewItems.Add(new ReviewItem("Generated files",
            generatedFiles.Count == 0 ? "None" : string.Join(", ", generatedFiles)));
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
        if (!string.IsNullOrWhiteSpace(LocalExportPath))
        {
            ReviewItems.Add(new ReviewItem("Local export path", LocalExportPath.Trim()));
        }
        ReviewItems.Add(new ReviewItem("Schedule", BuildSchedule().Describe()));
        if (RunOnStartup)
        {
            ReviewItems.Add(new ReviewItem("Run on service startup", "Yes"));
        }
        if (MaxParallelWorkers != 0 || LockTimeoutSeconds != 0 || QueryTimeoutSeconds != 120 || !IncrementalScripting)
        {
            var workers = MaxParallelWorkers == 0 ? "auto" : MaxParallelWorkers.ToString();
            var lockText = LockTimeoutSeconds == 0 ? "server default" : $"{LockTimeoutSeconds}s";
            var incremental = IncrementalScripting ? string.Empty : " · incremental scripting off";
            ReviewItems.Add(new ReviewItem("Advanced", $"{workers} workers · query {QueryTimeoutSeconds}s · lock {lockText}{incremental}"));
        }
        ReviewItems.Add(new ReviewItem("On changes", RunOnlyIfChanges
            ? "Commit only when changes are detected"
            : "Commit even when nothing changed (empty heartbeat commit)"));
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

    // Parse the schema-filter textbox into the case-insensitive allow-list the engine consumes.
    private HashSet<string> ParseSchemaFilter() => new(
        SchemaFilter.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    // Parse the reviewers textbox: split on commas/whitespace/semicolons, strip a leading '@',
    // drop blanks, de-dupe case-insensitively.
    private static List<string> ParseReviewers(string raw) =>
        [.. raw.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.TrimStart('@'))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private string EffectiveDestinationFolder(IReadOnlyList<string> databases) =>
        !string.IsNullOrWhiteSpace(DestinationFolder)
            ? DestinationFolder.Trim()
            // The dynamic scope always gets per-database subfolders from the engine, so its default
            // root stops at the server; a fixed list centers on the (first) database.
            : SyncAllUserDatabases
                ? $"environments/{SelectedConnection?.ServerName}"
                : $"environments/{SelectedConnection?.ServerName}/{(databases.Count > 0 ? databases[0] : "db")}";

    private ScheduleProfile BuildSchedule()
    {
        var time = TimeOnly.TryParseExact(TimeOfDay, "HH:mm", out var parsed) ? parsed : new TimeOnly(23, 0);
        var start = TimeOnly.TryParseExact(WindowStart, "HH:mm", out var s) ? s : new TimeOnly(22, 0);
        var end = TimeOnly.TryParseExact(WindowEnd, "HH:mm", out var e) ? e : new TimeOnly(5, 0);
        return new ScheduleProfile
        {
            Kind = SelectedScheduleKind,
            IntervalHours = Math.Max(1, IntervalHours),
            TimeOfDay = time,
            DayOfWeek = SelectedDayOfWeek,
            CronExpression = CronExpression,
            RunOnStartup = RunOnStartup,
            RunOnlyIfChanges = RunOnlyIfChanges,
            MaintenanceWindowEnabled = MaintenanceWindowEnabled,
            WindowStart = start,
            WindowEnd = end,
            DayScope = SelectedDayScope,
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

        // Job names must be unique (case-insensitively): History's job filter matches by name, so two
        // same-named jobs would have their runs conflated. Excludes the job being edited.
        var trimmedName = Name.Trim();
        var editingId = IsEditMode ? _editingJob?.Id : null;
        var existingJobs = await _jobs.GetAllAsync();
        if (existingJobs.Any(j => j.Id != editingId && string.Equals(j.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentStep = 1;
            StatusMessage = $"A job named \"{trimmedName}\" already exists — choose a different name.";
            return;
        }

        var selectedDatabases = Databases.Where(d => d.IsSelected).Select(d => d.Name).ToList();

        // When editing, mutate the existing job so fields the wizard does not surface
        // (Description, Enabled, RunSummary, the retry counts, and the non-preset Selection settings)
        // are preserved instead of being reset to defaults by the upsert.
        var job = IsEditMode && _editingJob is not null ? _editingJob : new SyncJob();
        job.Name = Name.Trim();
        job.ConnectionProfileId = SelectedConnection!.Id;
        // Export Only has no GitHub repository/branch; the git modes require one (validated above).
        job.RepositoryProfileId = IsExportOnly ? null : SelectedRepository!.Id;
        job.Branch = IsExportOnly ? null : (string.IsNullOrWhiteSpace(Branch) ? SelectedRepository!.DefaultBranch : Branch.Trim());
        job.ExportPath = IsExportOnly ? ExportPath.Trim() : null;
        // In the dynamic scope the checklist holds exclusions; the engine resolves the real list per run.
        job.DatabaseScope = SyncAllUserDatabases ? DatabaseScope.AllUserDatabases : DatabaseScope.SelectedDatabases;
        job.Databases = SyncAllUserDatabases ? [] : selectedDatabases;
        job.ExcludedDatabases = SyncAllUserDatabases ? selectedDatabases : [];
        job.DestinationFolder = EffectiveDestinationFolder(selectedDatabases);
        job.LocalExportPath = string.IsNullOrWhiteSpace(LocalExportPath) ? null : LocalExportPath.Trim();
        job.CommitMode = SelectedCommitMode;
        job.Reviewers = SelectedCommitMode == CommitMode.PullRequest ? ParseReviewers(Reviewers) : [];
        job.Tags = JobTags.Parse(Tags);
        job.Selection.Preset = SelectedPreset;
        if (IsCustomPreset)
        {
            job.Selection.CustomTypes = [.. ObjectTypes.Where(t => t.IsSelected).Select(t => t.Type)];
        }

        job.Selection.SchemaFilter = ParseSchemaFilter();
        job.Selection.ServerTypes = IncludeServerObjects
            ? [.. ServerObjectTypes.Where(t => t.IsSelected).Select(t => t.Type)]
            : [];
        job.Selection.ReferenceDataTables = IncludeReferenceData
            ? [.. ReferenceTables.Where(t => t.IsSelected).Select(t => t.QualifiedName)]
            : [];
        job.Selection.IncludeObjectInventory = IncludeObjectInventory;
        job.Selection.IncludeDatabaseOptions = IncludeDatabaseOptions;
        job.Selection.IncludeDatabasePermissionsFile = IncludePermissionsFile;
        job.Selection.IncludeDocumentation = IncludeDocumentation;
        job.Selection.IncludeSecurityReview = IncludeSecurityReview;
        job.Advanced.ReferenceDataMaxRows = Math.Max(1, ReferenceDataMaxRows);

        job.Schedule = BuildSchedule();
        // Mutate the existing Advanced object so the unsurfaced retry counts are preserved.
        job.Advanced.MaxParallelWorkers = Math.Max(0, MaxParallelWorkers);
        job.Advanced.SqlCommandTimeoutSeconds = Math.Max(1, QueryTimeoutSeconds);
        job.Advanced.SqlLockTimeoutSeconds = Math.Max(0, LockTimeoutSeconds);
        job.Advanced.IncrementalScripting = IncrementalScripting;
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

            // Stamp the intended next run so the UI shows it immediately; the scheduler refines it
            // with the Quartz-precise time on its next reconcile. Manual clears any leftover value.
            await _jobs.UpdateNextRunAtAsync(job.Id, ProvisionalNextRun(job));

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

    // The provisional next-run stamped at save. Standard cadences come from the profile; Cron is
    // computed here (never the stale pre-edit value — switching Daily→Cron used to freeze the old
    // cadence's next-run forever); Manual and disabled jobs clear it.
    private DateTimeOffset? ProvisionalNextRun(SyncJob job)
    {
        if (!job.Enabled || job.Schedule.Kind == ScheduleKind.Manual)
        {
            return null;
        }

        if (job.Schedule.Kind == ScheduleKind.Cron)
        {
            var cron = job.Schedule.CronExpression?.Trim();
            return !string.IsNullOrWhiteSpace(cron) && Quartz.CronExpression.IsValidExpression(cron)
                ? new Quartz.CronExpression(cron) { TimeZone = TimeZoneInfo.Local }.GetNextValidTimeAfter(_clock.UtcNow)
                : null;
        }

        return job.Schedule.GetNextRun(_clock.UtcNow);
    }
}
