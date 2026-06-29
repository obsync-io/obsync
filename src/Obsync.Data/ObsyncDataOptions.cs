namespace Obsync.Data;

/// <summary>Configuration for the local SQLite state database.</summary>
public sealed class ObsyncDataOptions
{
    /// <summary>Absolute path to the SQLite database file. Created if it does not exist.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Busy timeout (ms) applied to each connection, easing UI/Service contention.</summary>
    public int BusyTimeoutMs { get; set; } = 5000;
}
