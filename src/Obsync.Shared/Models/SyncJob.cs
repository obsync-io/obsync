namespace Obsync.Shared.Models;

/// <summary>Performance and reliability knobs surfaced under "Advanced settings".</summary>
public sealed class JobAdvancedOptions
{
    /// <summary>Max objects scripted concurrently per database. 0 = auto (based on CPU count).</summary>
    public int MaxParallelWorkers { get; set; }

    /// <summary>SQL command timeout per query, in seconds.</summary>
    public int SqlCommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// SQL lock timeout in seconds — how long a metadata read waits on a blocked resource before
    /// failing instead of hanging. 0 = don't set (use the server default / wait indefinitely).
    /// </summary>
    public int SqlLockTimeoutSeconds { get; set; }

    /// <summary>Number of attempts for transient SQL failures (deadlocks, timeouts, connection blips).</summary>
    public int SqlRetryCount { get; set; } = 3;

    /// <summary>Number of attempts for transient Git network failures (clone/fetch/push).</summary>
    public int GitRetryCount { get; set; } = 3;

    /// <summary>
    /// Row cap per reference-data table. A table with more rows is reported as a skip instead of
    /// being scripted — reference data is meant for lookup tables, not fact data.
    /// </summary>
    public int ReferenceDataMaxRows { get; set; } = 5000;

    /// <summary>
    /// Skip re-scripting objects whose <c>sys.objects.modify_date</c> is older than the last
    /// successful run's watermark. The big steady-state win on very large databases. Caveat:
    /// permission- and extended-property-only changes don't bump <c>modify_date</c> — the
    /// consolidated permissions file still captures grants every run, but a table file's INLINE
    /// grant/extended-property section can lag until the table itself changes; turning this off
    /// for one run captures those. (Index create/drop and trigger creation DO bump the table's
    /// modify_date, so they are picked up.) Export runs are always full snapshots.
    /// </summary>
    public bool IncrementalScripting { get; set; } = true;
}

/// <summary>A denormalized snapshot of a job's most recent run, for fast dashboard rendering.</summary>
public sealed class JobRunSummary
{
    public Guid? LastRunId { get; set; }
    public RunStatus? LastStatus { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public int? LastChangeCount { get; set; }
    public string? LastCommitSha { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>
/// The central product concept: a SQL Server source scripted, change-detected, committed,
/// and pushed to a GitHub repository on a schedule.
/// </summary>
public sealed class SyncJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Whether the schedule is active. Disabled jobs can still be run by hand.</summary>
    public bool Enabled { get; set; } = true;

    // --- Source ---
    public Guid ConnectionProfileId { get; set; }

    /// <summary>How the databases to script are chosen: a fixed list, or all user databases.</summary>
    public DatabaseScope DatabaseScope { get; set; } = DatabaseScope.SelectedDatabases;

    /// <summary>One or more databases to script. The design centers on one, but several are supported.
    /// Empty when <see cref="DatabaseScope"/> is <see cref="DatabaseScope.AllUserDatabases"/> — the
    /// engine resolves the live list at the start of each run.</summary>
    public List<string> Databases { get; set; } = [];

    /// <summary>Databases to leave out when <see cref="DatabaseScope"/> is
    /// <see cref="DatabaseScope.AllUserDatabases"/>. Compared case-insensitively.</summary>
    public List<string> ExcludedDatabases { get; set; } = [];

    // --- Destination ---

    /// <summary>The GitHub repository for the git modes; null for <see cref="CommitMode.ExportOnly"/>.</summary>
    public Guid? RepositoryProfileId { get; set; }

    /// <summary>Branch to commit to. Falls back to the repository's default branch when empty.</summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Repository-relative destination folder, e.g. <c>environments/prod/PROD-SQL01/SalesDB</c>.
    /// Object type folders are created beneath this.
    /// </summary>
    public string DestinationFolder { get; set; } = string.Empty;

    public CommitMode CommitMode { get; set; } = CommitMode.DirectCommit;

    /// <summary>Optional local path to also save the scripted output to.</summary>
    public string? LocalExportPath { get; set; }

    /// <summary>
    /// The destination for <see cref="CommitMode.ExportOnly"/> — a folder or a <c>.zip</c> path (a UNC
    /// path is allowed, for a network share). Null for the git modes.
    /// </summary>
    public string? ExportPath { get; set; }

    /// <summary>
    /// GitHub usernames to request as reviewers on the pull request (<see cref="CommitMode.PullRequest"/>
    /// only). Empty for direct-commit jobs. Requesting a review is best-effort — an unknown username
    /// is logged as a warning and never fails the run.
    /// </summary>
    public List<string> Reviewers { get; set; } = [];

    /// <summary>
    /// Free-form environment tags (e.g. <c>prod</c>, <c>finance</c>, <c>pci</c>), shown as chips
    /// wherever the job appears. A tag matching the configured production markers arms the Run-Now
    /// guard. Normalized (trimmed, de-duped) on save via <see cref="JobTags.Parse"/>.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    // --- What &amp; when ---
    public ObjectSelectionProfile Selection { get; set; } = new();
    public ScheduleProfile Schedule { get; set; } = new();
    public JobAdvancedOptions Advanced { get; set; } = new();

    // --- Cached run summary (updated after each run) ---
    public JobRunSummary RunSummary { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Comma-separated database list (or the dynamic-scope description), for display in lists and tables.</summary>
    public string DatabasesDisplay => DatabaseScope == DatabaseScope.AllUserDatabases
        ? ExcludedDatabases.Count > 0 ? $"All user databases (excl. {string.Join(", ", ExcludedDatabases)})" : "All user databases"
        : string.Join(", ", Databases);

    // --- Transient display fields ---
    // Populated by list view models after resolving the connection/repository profiles. These are
    // NOT persisted (JobRepository maps columns explicitly and never serializes the whole SyncJob).

    /// <summary>Human-readable source (SQL Server) for list/table views. Not persisted.</summary>
    public string? SourceDisplay { get; set; }

    /// <summary>Human-readable destination (repository · branch) for list/table views. Not persisted.</summary>
    public string? DestinationDisplay { get; set; }

    /// <summary>
    /// The job's tags classified against the current production markers, for chip rendering. Filled by
    /// list view models via <see cref="JobTags.Classify"/>. Not persisted.
    /// </summary>
    public IReadOnlyList<TagChip> TagChips { get; set; } = [];

    /// <summary>
    /// True while a run for this job is in progress. Set by the app's run coordinator when list
    /// view models refresh. Not persisted.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// The status to show in lists: "Running" while a run is in progress, otherwise the last run's
    /// status. Keeps the running indicator visible everywhere and across navigation. Not persisted.
    /// </summary>
    public RunStatus? DisplayStatus => IsRunning ? RunStatus.Running : RunSummary.LastStatus;

    /// <summary>
    /// True when the status badge should read "Paused" instead of the last run's status: the
    /// schedule is disabled and no run is in progress. A presentation concept, not a
    /// <see cref="RunStatus"/> — pausing never rewrites run history. Not persisted.
    /// </summary>
    public bool IsPaused => !Enabled && !IsRunning;

    /// <summary>
    /// Result of <see cref="IsScheduleOverdue"/> at the time the owning list was loaded, cached so
    /// views can bind it without reading a clock per cell. Not persisted.
    /// </summary>
    public bool IsOverdue { get; set; }

    /// <summary>How far past its cached next-run time a schedule may drift before it counts as
    /// overdue — absorbs scheduler startup, reconcile latency, and run-lock waits.</summary>
    public static readonly TimeSpan ScheduleOverdueGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether this job's schedule has visibly stalled at <paramref name="now"/>: it is enabled and
    /// scheduled, its cached next-run time passed more than <see cref="ScheduleOverdueGrace"/> ago,
    /// and no run is in progress. Takes the clock as a parameter so the rule is deterministic.
    /// </summary>
    public bool IsScheduleOverdue(DateTimeOffset now) =>
        Enabled
        && Schedule.Kind != ScheduleKind.Manual
        && !IsRunning
        && RunSummary.NextRunAt is { } next
        && next < now - ScheduleOverdueGrace;
}
