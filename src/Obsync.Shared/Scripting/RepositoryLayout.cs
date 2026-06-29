namespace Obsync.Shared.Scripting;

/// <summary>
/// Helpers for composing repository-relative paths beneath a job's destination folder, and
/// the well-known metadata file locations written alongside the scripted objects.
/// </summary>
public static class RepositoryLayout
{
    /// <summary>Folder holding run metadata (object inventory, database options).</summary>
    public const string MetadataFolder = "metadata";

    /// <summary>JSON inventory of every tracked object, relative to the database root.</summary>
    public const string ObjectInventoryFile = "metadata/object-inventory.json";

    /// <summary>Scripted database-level options, relative to the database root.</summary>
    public const string DatabaseOptionsFile = "metadata/database-options.sql";

    /// <summary>
    /// Consolidated database-scoped GRANT/DENY permission script, relative to the database root.
    /// (Object-level permissions are also scripted inline within each object's own file.)
    /// </summary>
    public const string PermissionsFile = "security/permissions/permissions.sql";

    /// <summary>
    /// Joins a base folder and a relative path into a clean, forward-slashed repository path
    /// with no leading, trailing, or duplicate separators.
    /// </summary>
    public static string Combine(string? baseFolder, string relativePath)
    {
        var left = (baseFolder ?? string.Empty).Replace('\\', '/').Trim('/');
        var right = relativePath.Replace('\\', '/').Trim('/');

        if (left.Length == 0)
        {
            return right;
        }

        return right.Length == 0 ? left : $"{left}/{right}";
    }
}
