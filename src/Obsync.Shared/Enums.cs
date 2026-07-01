namespace Obsync.Shared;

/// <summary>How Obsync authenticates to a SQL Server instance.</summary>
public enum SqlAuthenticationMode
{
    /// <summary>Windows Integrated Authentication (current process identity / Kerberos / NTLM).</summary>
    WindowsIntegrated = 0,

    /// <summary>SQL Server login with a username and password.</summary>
    SqlLogin = 1,

    // Azure AD / Entra ID is planned for a later release.
}

/// <summary>The outcome of the most recent connectivity test for a server profile.</summary>
public enum ConnectionTestStatus
{
    /// <summary>The connection has not been tested yet.</summary>
    Untested = 0,

    /// <summary>The last test reached the server successfully.</summary>
    Connected = 1,

    /// <summary>The last test failed to reach the server.</summary>
    Failed = 2,
}

/// <summary>How Obsync authenticates to GitHub.</summary>
public enum GitHubAuthMode
{
    /// <summary>Fine-grained Personal Access Token (MVP).</summary>
    PersonalAccessToken = 0,

    // GitHub App and SSH/Git CLI modes are planned for later releases.
}

/// <summary>How a job's commits land in the destination repository.</summary>
public enum CommitMode
{
    /// <summary>Commit directly to the configured branch and push.</summary>
    DirectCommit = 0,

    /// <summary>Open a pull request instead of committing to the branch (planned).</summary>
    PullRequest = 1,
}

/// <summary>The cadence on which a sync job runs.</summary>
public enum ScheduleKind
{
    /// <summary>Runs only when triggered by hand.</summary>
    Manual = 0,
    Hourly = 1,
    Daily = 2,
    Weekly = 3,

    /// <summary>Driven by a custom cron expression.</summary>
    Cron = 4,
}

/// <summary>What initiated a run.</summary>
public enum RunTrigger
{
    Manual = 0,
    Scheduled = 1,
    Startup = 2,
}

/// <summary>The outcome of a sync run.</summary>
public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,

    /// <summary>Completed successfully but produced no changes to commit.</summary>
    NoChanges = 3,

    /// <summary>Completed, but one or more objects failed or were skipped.</summary>
    Warning = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>The kind of change detected for an object during a run.</summary>
public enum ChangeType
{
    Unchanged = 0,
    Added = 1,
    Modified = 2,
    Deleted = 3,
}

/// <summary>Severity for user-facing run log entries.</summary>
public enum SyncLogLevel
{
    /// <summary>Friendly, high-level progress shown by default.</summary>
    Info = 0,
    Warning = 1,
    Error = 2,

    /// <summary>Detailed technical entry, shown only when the user expands logs.</summary>
    Debug = 3,
}

/// <summary>Which engine path produces the script for an object type.</summary>
public enum ScriptingStrategy
{
    /// <summary>Scripted from raw SQL Server metadata (fast path).</summary>
    Metadata = 0,

    /// <summary>Scripted via SQL Server Management Objects (high fidelity).</summary>
    Smo = 1,
}

/// <summary>Preset bundles of object types offered in the Create Job wizard.</summary>
public enum ObjectSelectionPreset
{
    /// <summary>The common, recommended set for most teams.</summary>
    Recommended = 0,

    /// <summary>Stored procedures, views, functions, and triggers only.</summary>
    ProgrammabilityOnly = 1,

    /// <summary>Every supported object type for a full schema snapshot.</summary>
    FullSchema = 2,

    /// <summary>A user-defined selection.</summary>
    Custom = 3,
}
