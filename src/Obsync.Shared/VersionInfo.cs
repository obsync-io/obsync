using System.Reflection;

namespace Obsync.Shared;

/// <summary>Reads the informational version of an assembly, for display and support bundles.</summary>
public static class VersionInfo
{
    /// <summary>
    /// The assembly's version as a clean string (e.g. <c>0.1.0</c>): its
    /// <see cref="AssemblyInformationalVersionAttribute"/> with any <c>+&lt;gitsha&gt;</c> build
    /// metadata stripped, falling back to the assembly version.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
