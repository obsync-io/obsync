namespace Obsync.Data;

/// <summary>Configuration for the local SQLite state database.</summary>
public sealed class ObsyncDataOptions
{
    /// <summary>Absolute path to the SQLite database file. Created if it does not exist.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Busy timeout (ms) applied to each connection, easing UI/Service contention.</summary>
    // 30s, not the SQLite-ish 5s default: a VLDB state batch holds one write transaction for
    // multiple seconds, and the app and service write concurrently — a shorter timeout surfaces
    // spurious "database is locked" failures under exactly that overlap.
    public int BusyTimeoutMs { get; set; } = 30000;
}
