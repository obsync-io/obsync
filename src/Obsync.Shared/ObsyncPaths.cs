namespace Obsync.Shared;

/// <summary>Well-known local paths used by all Obsync hosts (app, service, CLI).</summary>
public static class ObsyncPaths
{
    /// <summary>Root data folder, e.g. <c>%LOCALAPPDATA%\Obsync</c>.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obsync");

    /// <summary>The local SQLite state database file.</summary>
    public static string DatabasePath => Path.Combine(Root, "obsync.db");

    /// <summary>Root folder under which per-repository Git workspaces are cloned.</summary>
    public static string WorkspacesRoot => Path.Combine(Root, "workspaces");

    /// <summary>Folder for rolling log files.</summary>
    public static string LogsRoot => Path.Combine(Root, "logs");

    /// <summary>Folder for per-job run lock files (see <see cref="JobRunLock"/>).</summary>
    public static string LocksRoot => Path.Combine(Root, "locks");

    /// <summary>Ensures the data directories exist.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WorkspacesRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(LocksRoot);
    }
}
