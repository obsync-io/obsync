using System.Reflection;

namespace Obsync.Service;

/// <summary>
/// Verifies that every Obsync assembly beside the service host came from the same build.
/// </summary>
/// <remarks>
/// This exists for one specific, reachable state: a half-applied upgrade.
///
/// <para>
/// All three hosts publish self-contained into ONE flat folder, so <c>Obsync.Engine.dll</c>,
/// <c>Obsync.Data.dll</c> and the rest are shared by the app, the service and the CLI. During an
/// upgrade the MSI stops and deletes the service first, which unlocks <c>Obsync.Service.exe</c> —
/// but if the user leaves the desktop app running and answers "Do not close applications" to the
/// files-in-use prompt, the app keeps those shared DLLs mapped. MSI cannot overwrite them, so it
/// defers them to a reboot the user may never get around to, while the freshly-written service
/// binary is created and started immediately.
/// </para>
///
/// <para>
/// The result is a NEW service host running against OLD shared libraries, executing scheduled syncs
/// — including the catch-up run issued at startup — against customer databases and repositories. It
/// may throw <see cref="TypeLoadException"/> somewhere useful, or it may not throw at all and simply
/// behave like a version nobody tested. Checked here so it always fails immediately, loudly, and
/// with a message that names the actual remedy.
/// </para>
///
/// <para>
/// Versions are read straight off the files with <see cref="AssemblyName.GetAssemblyName"/>, which
/// reads metadata without loading anything into the runtime. That matters: touching a type from a
/// mismatched assembly is the very failure being detected, so the detector must not do it.
/// </para>
/// </remarks>
internal static class BuildIntegrity
{
    /// <summary>
    /// Describes the version skew beside the running host, or null when everything agrees.
    /// </summary>
    /// <param name="directory">Folder to inspect; defaults to the host's own directory.</param>
    /// <param name="expected">Version every Obsync assembly must carry; defaults to this host's.</param>
    public static string? DescribeMismatch(string? directory = null, Version? expected = null)
    {
        directory ??= AppContext.BaseDirectory;
        expected ??= typeof(BuildIntegrity).Assembly.GetName().Version;
        if (expected is null || !Directory.Exists(directory))
        {
            return null;
        }

        var mismatches = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "Obsync.*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            Version? version;
            try
            {
                version = AssemblyName.GetAssemblyName(path).Version;
            }
            catch (BadImageFormatException)
            {
                // A native or resource-only file that merely matches the name pattern.
                continue;
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            if (version is not null && version != expected)
            {
                mismatches.Add($"{Path.GetFileName(path)} is {version}");
            }
        }

        if (mismatches.Count == 0)
        {
            return null;
        }

        return $"This installation is only half upgraded. The service host is {expected}, but "
            + $"{string.Join(", ", mismatches)}. This happens when an upgrade could not replace files "
            + "that a running Obsync window still had open, so Windows deferred them to the next "
            + "restart. Refusing to start rather than run sync jobs on mismatched components — "
            + "restart the computer to complete the upgrade.";
    }
}
