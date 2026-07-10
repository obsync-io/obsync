using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Obsync.Data;

/// <summary>Applies pending embedded SQL migrations to the local state database.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDatabaseInitializer" />
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private const string MigrationPrefix = "Obsync.Data.Migrations.";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbConnectionFactory connectionFactory, ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Run migrations with foreign keys disabled so a table-rebuild migration (create-new, copy,
        // DROP the old referenced table, rename) succeeds. The pragma is connection-scoped and a no-op
        // inside a transaction, so it is set here, before the per-migration transactions; this
        // init-only connection is disposed at the end. Runtime connections still enable foreign keys
        // (SqliteConnectionFactory sets them ON per connection), so enforcement is unchanged.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken).ConfigureAwait(false);

        // The app and the service can initialize concurrently (the installer starts the service and
        // launches the app back-to-back): give the loser enough patience to sit out a slow
        // table-rebuild migration instead of failing its startup with SQLITE_BUSY.
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 60000;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "CREATE TABLE IF NOT EXISTS __migrations (version TEXT NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);

        var applied = await GetAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var (version, sql) in LoadMigrations())
        {
            if (applied.Contains(version))
            {
                continue;
            }

            // BeginTransaction (non-deferred) takes the write lock up front, so concurrent
            // initializers serialize here rather than mid-migration.
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                // Re-check under the write lock: the pre-scan above ran before the lock, so a
                // concurrent initializer may have applied this migration while we waited. Without
                // this the loser re-runs the migration and fails its host's startup with
                // "table/index already exists".
                if (await IsAppliedAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await ExecuteAsync(connection, sql, cancellationToken, transaction).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    "INSERT INTO __migrations (version, applied_at) VALUES ($v, $t);",
                    cancellationToken,
                    transaction,
                    ("$v", version),
                    ("$t", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Applied database migration {Version}.", version);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<bool> IsAppliedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM __migrations WHERE version = $v;";
        command.Parameters.AddWithValue("$v", version);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
    }

    private static IEnumerable<(string Version, string Sql)> LoadMigrations()
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal) &&
                        n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration '{name}' could not be loaded.");
            using var reader = new StreamReader(stream);
            var version = name[MigrationPrefix.Length..^".sql".Length];
            yield return (version, reader.ReadToEnd());
        }
    }

    private static async Task<HashSet<string>> GetAppliedVersionsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM __migrations;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetString(0));
        }

        return versions;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
