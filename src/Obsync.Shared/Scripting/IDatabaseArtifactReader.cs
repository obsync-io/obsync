using Obsync.Shared.Models;

namespace Obsync.Shared.Scripting;

/// <summary>
/// Reads database-scoped artifacts that are not individual objects: the database option set and
/// the consolidated permission grants. Implementations issue a single bulk query per artifact and
/// return deterministic, timestamp-free script text suitable for hashing and version control.
/// </summary>
public interface IDatabaseArtifactReader
{
    /// <summary>Builds the <c>ALTER DATABASE … SET</c> settings script (no file paths or sizes).</summary>
    Task<string> ReadDatabaseOptionsAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default);

    /// <summary>Builds the consolidated database-scoped GRANT/DENY script from <c>sys.database_permissions</c>.</summary>
    Task<string> ReadPermissionsAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default);
}
