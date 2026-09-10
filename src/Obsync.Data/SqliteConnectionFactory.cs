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
                // 64 MB of page cache, against SQLite's 2 MB default.
                //
                // A million-object upsert maintains a table B-tree and a wide unique index whose
                // interior pages alone run to tens of megabytes. At 2 MB those pages are evicted and
                // re-read continuously, so the write degrades into random IO. 64 MB is a rounding
                // error beside the estate this process already holds in managed memory, and it is a
                // cache — it costs nothing on the small installs that never need it. Negative means
                // kibibytes rather than pages, so it is independent of page_size.
                "PRAGMA cache_size = -65536;" +
                // Sorts and DISTINCTs that cannot use an index build their temp B-tree in memory
                // rather than in a file beside the database. The alternative is spilling a
                // million-row sort to disk, which is what the object search does today.
                "PRAGMA temp_store = MEMORY;" +
                $"PRAGMA busy_timeout = {_busyTimeoutMs};";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
