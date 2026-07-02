using Obsync.Shared.Objects;

namespace Obsync.Shared.Models;

/// <summary>A single execution of a sync job and its aggregate results.</summary>
public sealed class SyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable run key, e.g. <c>20260628-230000</c>. Used in commit bodies.</summary>
    public string RunKey { get; set; } = string.Empty;

    public Guid JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public RunTrigger Trigger { get; set; }

    /// <summary>
    /// The identity that started the run (as <c>DOMAIN\user</c>): the interactive user for an
    /// app run, or the service account for a scheduled run. Set once at insert; never updated.
    /// </summary>
    public string? TriggeredBy { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public string ServerName { get; set; } = string.Empty;

    /// <summary>Comma-separated databases covered by the run.</summary>
    public string Databases { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long DurationMs { get; set; }

    public int ObjectsScanned { get; set; }
    public int ObjectsAdded { get; set; }
    public int ObjectsModified { get; set; }
    public int ObjectsDeleted { get; set; }
    public int ObjectsFailed { get; set; }

    public string? CommitSha { get; set; }
    public string? CommitUrl { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Total changed objects (added + modified + deleted).</summary>
    public int ChangeCount => ObjectsAdded + ObjectsModified + ObjectsDeleted;
}

/// <summary>A user-facing log entry for a run. Friendly by default; technical detail is opt-in.</summary>
public sealed class SyncRunLog
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public SyncLogLevel Level { get; set; } = SyncLogLevel.Info;

    /// <summary>Friendly message, e.g. "Scanned 42,120 objects".</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional technical detail shown when the user expands logs.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// The persisted tracking state for one object under a job: its last hash, file path, and
/// commit metadata. This is what change detection compares against between runs.
/// </summary>
public sealed class TrackedObjectState
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public SqlObjectType ObjectType { get; set; }
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public int? ObjectId { get; set; }

    /// <summary>Repository-relative path of the object's file.</summary>
    public string FilePath { get; set; } = string.Empty;

    public string LastHash { get; set; } = string.Empty;
    public DateTimeOffset LastScriptedAt { get; set; }
    public DateTimeOffset? LastCommittedAt { get; set; }
    public string? LastCommitSha { get; set; }
    public Guid? LastRunId { get; set; }
    public RunStatus LastStatus { get; set; }
    public string? ErrorMessage { get; set; }
}
