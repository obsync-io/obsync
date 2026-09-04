namespace Obsync.Shared;

/// <summary>Well-known local paths used by all Obsync hosts (app, service, CLI).</summary>
public static class ObsyncPaths
{
    /// <summary>
    /// Root data folder, e.g. <c>%LOCALAPPDATA%\Obsync</c>. The <c>OBSYNC_DATA_ROOT</c>
    /// environment variable overrides it — for isolated test harnesses and for deployments that
    /// must relocate the data root. (Note: <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
    /// resolves via the shell's known-folder API and deliberately ignores a <c>%LOCALAPPDATA%</c>
    /// env-var override, so this dedicated variable is the only reliable redirection.)
    /// </summary>
    public static string Root { get; } = ResolveRoot(Environment.GetEnvironmentVariable("OBSYNC_DATA_ROOT"));

    /// <summary>
    /// Applies the <c>OBSYNC_DATA_ROOT</c> override, or falls back to the per-user default.
    /// <para>
    /// The override must be FULLY QUALIFIED. A relative value resolves against the current
    /// directory, which differs per host — the app's install folder versus <c>C:\Windows\System32</c>
    /// for a service — so the two hosts would silently use different databases, and the app would
    /// then report the service as running under the wrong account. Surrounding quotes and
    /// whitespace are tolerated because this is typically pasted into the Windows environment
    /// editor. Anything unusable falls back to the default rather than throwing: this runs in a
    /// static initializer, so throwing here kills the process before any logger exists.
    /// </para>
    /// <para>
    /// Note it must also be a MACHINE-scoped variable to reach a service — a service process
    /// inherits only the system environment.
    /// </para>
    /// </summary>
    internal static string ResolveRoot(string? overrideRoot)
    {
        var trimmed = overrideRoot?.Trim().Trim('"').Trim();
        if (!string.IsNullOrEmpty(trimmed) && Path.IsPathFullyQualified(trimmed))
        {
            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Unusable path — fall through to the default.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obsync");
    }

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
