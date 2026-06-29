namespace Obsync.Shared.Models;

/// <summary>Performance and reliability knobs surfaced under "Advanced settings".</summary>
public sealed class JobAdvancedOptions
{
    /// <summary>Max databases/object-types scripted concurrently. 0 = auto (based on CPU count).</summary>
    public int MaxParallelWorkers { get; set; }

    /// <summary>SQL command timeout per query, in seconds.</summary>
    public int SqlCommandTimeoutSeconds { get; set; } = 120;

    /// <summary>Number of retries for transient Git/GitHub failures.</summary>
    public int GitRetryCount { get; set; } = 3;
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

    /// <summary>One or more databases to script. The design centers on one, but several are supported.</summary>
    public List<string> Databases { get; set; } = [];

    // --- Destination ---
    public Guid RepositoryProfileId { get; set; }

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

    // --- What &amp; when ---
    public ObjectSelectionProfile Selection { get; set; } = new();
    public ScheduleProfile Schedule { get; set; } = new();
    public JobAdvancedOptions Advanced { get; set; } = new();

    // --- Cached run summary (updated after each run) ---
    public JobRunSummary RunSummary { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Comma-separated database list, for display in lists and tables.</summary>
    public string DatabasesDisplay => string.Join(", ", Databases);
}
