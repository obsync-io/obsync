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
    /// Set when <see cref="Root"/> could not be resolved the normal way and a fallback was used.
    /// Null in every healthy deployment. Surfaced by diagnostics and the support bundle, because
    /// the condition is otherwise invisible: the service starts, heartbeats, and reports itself
    /// perfectly healthy against a database the app will never look at.
    /// </summary>
    public static string? RootResolutionWarning { get; private set; }

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

        return DefaultRoot();
    }

    /// <summary>
    /// The per-user default, guaranteed FULLY QUALIFIED.
    /// <para>
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns
    /// <see cref="string.Empty"/> — it does not throw — when the folder cannot be resolved, which is
    /// the ordinary result for a service identity whose profile hive is not loaded (a gMSA or
    /// managed account, or a profile removed by the "delete profiles older than N days" policy).
    /// <c>Path.Combine("", "Obsync")</c> then yields the RELATIVE path <c>"Obsync"</c>, and a
    /// service's working directory is <c>C:\Windows\System32</c> — so the service silently created
    /// and heartbeated into <c>C:\Windows\System32\Obsync</c> while the app never saw it, and no
    /// amount of correcting the logon account helped. Being relative, it also re-targeted the
    /// database, locks and workspaces if anything changed the process working directory mid-life.
    /// </para>
    /// <para>
    /// The override path has enforced <see cref="Path.IsPathFullyQualified"/> since it was written,
    /// for exactly this reason; the fallback simply never got the same guard. Each step below is
    /// tried in turn and the first fully-qualified answer wins, so a healthy deployment is
    /// unchanged. Reaching past the first step is abnormal and is recorded in
    /// <see cref="RootResolutionWarning"/> rather than thrown: this runs in a static initializer,
    /// where a throw kills the host before any logger exists.
    /// </para>
    /// </summary>
    private static string DefaultRoot()
    {
        var root = DefaultRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetEnvironmentVariable("USERPROFILE"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            out var warning);
        RootResolutionWarning = warning;
        return root;
    }

    /// <summary>
    /// The resolution itself, with every input passed in. Separated so the fallbacks are directly
    /// testable: on a healthy machine the first step always succeeds, so a test that reads the real
    /// environment can never reach the branches that matter — which is exactly why the original
    /// <c>NoOverride_UsesThePerUserDefault</c> test passed even when both sides evaluated to the
    /// relative string <c>"Obsync"</c>.
    /// </summary>
    internal static string DefaultRoot(
        string? knownFolder, string? localAppData, string? userProfile, string? commonAppData, out string? warning)
    {
        warning = null;
        if (Qualified(knownFolder) is { } fromKnownFolder)
        {
            return Path.Combine(fromKnownFolder, "Obsync");
        }

        // The environment variable is not equivalent to the known folder (the shell API ignores it
        // deliberately), but when the known folder is unavailable it is the best remaining record
        // of where this account's local data belongs.
        if (Qualified(localAppData) is { } fromEnvironment)
        {
            warning =
                "The Local Application Data folder could not be resolved for this account, so Obsync fell back to "
                + "the LOCALAPPDATA environment variable. This usually means the account's Windows profile is not "
                + "loaded — common for a managed service account.";
            return Path.Combine(fromEnvironment, "Obsync");
        }

        if (Qualified(userProfile) is { } fromProfile)
        {
            warning =
                "The Local Application Data folder could not be resolved for this account, so Obsync fell back to "
                + "the user profile folder. This usually means the account's Windows profile is not loaded.";
            return Path.Combine(fromProfile, "AppData", "Local", "Obsync");
        }

        // Last resort: machine-wide, always fully qualified, and writable by a service. It is NOT
        // per-user, so hosts running as different accounts would share it — stated plainly in the
        // warning, because a shared root that is visible is far better than a relative path under
        // System32 that nobody can find.
        var root = Qualified(commonAppData) ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        warning =
            "No per-account data folder could be resolved, so Obsync is using a machine-wide folder under "
            + $"{root}. Every account on this machine shares it. Set OBSYNC_DATA_ROOT (machine-scoped) to choose "
            + "the location deliberately.";
        return Path.Combine(root, "Obsync");

        static string? Qualified(string? candidate) =>
            !string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) ? candidate : null;
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
