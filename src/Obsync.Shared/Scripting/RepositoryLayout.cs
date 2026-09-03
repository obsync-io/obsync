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
    /// Consolidated GRANT/DENY permission script (database, schema, object, column, and type
    /// scopes), relative to the database root. This file is the authoritative permission record:
    /// only SMO-scripted types (tables, users, UDTs, …) additionally carry grants inline in their
    /// own files — the metadata fast-path types (procedures, views, functions, …) do not.
    /// </summary>
    public const string PermissionsFile = "security/permissions/permissions.sql";

    /// <summary>Folder holding versioned reference/static table data, relative to the database root.</summary>
    public const string DataFolder = "data";

    /// <summary>Generated markdown documentation (object index + data dictionary), relative to the database root.</summary>
    public const string DocumentationFile = "docs/README.md";

    /// <summary>Generated database security review (markdown findings), relative to the database root.</summary>
    public const string SecurityReviewFile = "security/security-review.md";

    /// <summary>Generated server security review, relative to the job's destination folder.</summary>
    public const string ServerSecurityReviewFile = "server/security-review.md";

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
    /// <summary>
    /// Resolves a repository-relative path to an absolute one beneath <paramref name="root"/>, or
    /// returns null when it would land outside. Every write, copy and delete goes through this, so
    /// a name that escapes composition — a database called <c>..\..\Windows\Temp</c>, a hand-edited
    /// destination folder, or a stale state row written by an older build — cannot reach the
    /// filesystem.
    /// </summary>
    /// <remarks>
    /// Three details this depends on, all of which are easy to get wrong:
    /// <list type="bullet">
    /// <item><description><see cref="Path.Combine(string, string)"/> is NOT usable here: it
    /// silently discards its left operand when the right one is rooted, so
    /// <c>Combine(workspace, @"C:\Windows\x")</c> returns <c>C:\Windows\x</c>. The two-argument
    /// <see cref="Path.GetFullPath(string, string)"/> resolves against the base and lets the
    /// containment test below reject it.</description></item>
    /// <item><description>The prefix test needs the trailing separator. A bare
    /// <c>StartsWith(root)</c> accepts <c>C:\ws2\evil.sql</c> as being inside <c>C:\ws</c>.</description></item>
    /// <item><description>The comparison must be case-insensitive: NTFS is, so
    /// <c>C:\WS\a.sql</c> genuinely is the same location as <c>C:\ws\a.sql</c> and an ordinal
    /// comparison would reject a legitimate path.</description></item>
    /// </list>
    /// Symlinks and junctions are deliberately not resolved: it would cost a syscall per component
    /// on a path this hot, and the workspace is a clone Obsync creates itself.
    /// </remarks>
    public static string? ResolveWithin(string root, string relativePath)
    {
        string rootFull;
        string full;
        try
        {
            rootFull = Path.GetFullPath(root);
            full = Path.GetFullPath(relativePath, rootFull);
        }
        catch (ArgumentException)
        {
            // An embedded NUL, or a root that is not fully qualified.
            return null;
        }

        // Trim first: GetFullPath preserves a trailing separator on the root, and appending a
        // second one would build "C:\ws\\" and reject everything.
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // Strictly inside: a relative path that collapses to the root itself ("", ".", "..") means
        // the composition is broken, not that the write is legitimate.
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

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
