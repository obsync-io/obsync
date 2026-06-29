using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Obsync.Data;

/// <summary>Opens configured, ready-to-use connections to the local state database.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Opens a new connection with WAL, foreign keys, and busy timeout applied.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDbConnectionFactory" />
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;

    public SqliteConnectionFactory(IOptions<ObsyncDataOptions> options)
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.DatabasePath))
        {
            throw new InvalidOperationException("ObsyncDataOptions.DatabasePath must be configured.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(value.DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = value.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        _busyTimeoutMs = value.BusyTimeoutMs;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode = WAL;" +
                "PRAGMA foreign_keys = ON;" +
                $"PRAGMA busy_timeout = {_busyTimeoutMs};";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
