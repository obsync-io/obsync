using Microsoft.Data.SqlClient;

namespace Obsync.Metadata;

/// <summary>Session-level SQL options applied once after a connection opens.</summary>
internal static class SqlSession
{
    /// <summary>
    /// Applies <c>SET LOCK_TIMEOUT</c> so metadata reads fail fast rather than blocking indefinitely on a
    /// busy server. No-op when <paramref name="lockTimeoutSeconds"/> is 0 or less (server default).
    /// </summary>
    public static async Task ApplyLockTimeoutAsync(
        SqlConnection connection, int lockTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (lockTimeoutSeconds <= 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SET LOCK_TIMEOUT {lockTimeoutSeconds * 1000};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
