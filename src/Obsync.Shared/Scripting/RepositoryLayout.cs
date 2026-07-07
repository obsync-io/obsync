namespace Obsync.Shared.Scripting;

/// <summary>
/// Helpers for composing repository-relative paths beneath a job's destination folder, and
/// the well-known metadata file locations written alongside the scripted objects.
/// </summary>
public static class RepositoryLayout
{
    /// <summary>Folder holding run metadata (object inventory, database options).</summary>
    public const string MetadataFolder = "metadata";

    /// <summary>Optional user-authored ignore file, relative to the database root (like .gitignore).</summary>
    public const string IgnoreFile = ".obsyncignore";

    /// <summary>JSON inventory of every tracked object, relative to the database root.</summary>
    public const string ObjectInventoryFile = "metadata/object-inventory.json";

    /// <summary>Scripted database-level options, relative to the database root.</summary>
    public const string DatabaseOptionsFile = "metadata/database-options.sql";

    /// <summary>
    /// Consolidated database-scoped GRANT/DENY permission script, relative to the database root.
    /// (Object-level permissions are also scripted inline within each object's own file.)
    /// </summary>
    public const string PermissionsFile = "security/permissions/permissions.sql";

    /// <summary>Folder holding versioned reference/static table data, relative to the database root.</summary>
    public const string DataFolder = "data";

    /// <summary>Generated markdown documentation (object index + data dictionary), relative to the database root.</summary>
    public const string DocumentationFile = "docs/README.md";

    /// <summary>Folder holding server-level (instance-scoped) objects, relative to the job's destination folder.</summary>
    public const string ServerFolder = "server";

    /// <summary>Scripted server-level <c>sp_configure</c> values, relative to the job's destination folder.</summary>
    public const string ServerConfigurationFile = "server/server-configuration.sql";

    /// <summary>
    /// Sentinel "database" name scoping server-level rows in per-database state (which requires a
    /// non-null database name). A <c>$</c> is technically legal inside a bracketed database
    /// identifier, but the sentinel is vanishingly unlikely to collide with a real database name
    /// and is validated nowhere else.
    /// </summary>
    public const string ServerScopeName = "$server";

    /// <summary>
    /// The repository path for one reference table's data script, e.g. <c>data/dbo.Currency.sql</c>.
    /// Characters that are invalid in file names are replaced with <c>_</c>.
    /// </summary>
    public static string ReferenceDataFile(string schema, string table) =>
        $"{DataFolder}/{SanitizeFileStem($"{schema}.{table}")}.sql";

    private static string SanitizeFileStem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string([.. name.Select(ch => invalid.Contains(ch) ? '_' : ch)]).TrimEnd('.', ' ');
        return stem.Length == 0 ? "_" : stem;
    }

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
