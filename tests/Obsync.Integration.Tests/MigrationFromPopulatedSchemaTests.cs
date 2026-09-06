using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Obsync.Data;
using Obsync.Data.DependencyInjection;

namespace Obsync.Integration.Tests;

/// <summary>
/// Migrates a database that already holds data, the way a real upgrade does.
/// </summary>
/// <remarks>
/// Every other test in this repository initializes against a brand-new temp file, so the whole
/// V001..V013 chain was only ever exercised as "create everything in order on an empty database".
/// That is the one shape an upgrade never has. The two migrations that rebuild rather than append —
/// <c>V005</c>, which rebuilds the jobs table, and <c>V011</c>, which de-duplicates
/// <c>object_states</c> and swaps a BINARY unique index for a NOCASE one — had therefore never run
/// against a populated table anywhere, including CI.
///
/// <para>
/// The data root lives outside the folder the MSI deletes, so this database is exactly what survives
/// an upgrade and what the new binaries then migrate. Losing a row here means losing a customer's
/// job definitions.
/// </para>
/// </remarks>
public sealed class MigrationFromPopulatedSchemaTests : IDisposable
{
    private const string MigrationPrefix = "Obsync.Data.Migrations.";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-upgrade-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Every embedded migration, in the order the initializer applies them.</summary>
    private static IReadOnlyList<(string Version, string Sql)> AllMigrations()
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[MigrationPrefix.Length..^".sql".Length], reader.ReadToEnd());
            })
            .ToList();
    }

    /// <summary>Builds a database at the schema an OLD version of Obsync would have left behind.</summary>
    private async Task CreateSchemaAsOfAsync(string throughVersion)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await Exec(connection, "PRAGMA foreign_keys = OFF;");
        await Exec(
            connection,
            "CREATE TABLE IF NOT EXISTS __migrations (version TEXT NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);");

        foreach (var (version, sql) in AllMigrations())
        {
            await Exec(connection, sql);
            await Exec(
                connection,
                $"INSERT INTO __migrations (version, applied_at) VALUES ('{version}', '{DateTimeOffset.UtcNow:O}');");

            if (string.Equals(version, throughVersion, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException($"No migration named '{throughVersion}'.");
    }

    private static async Task Exec(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddObsyncData(_dbPath);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheFullChain_AppliesToADatabaseCreatedByTheVeryFirstRelease()
    {
        // V001 only, then everything after it — the widest upgrade the product can be asked to do.
        await CreateSchemaAsOfAsync("V001__init");

        await using var provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        Assert.Equal(
            (long)AllMigrations().Count,
            Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM __migrations;")));
    }

    [Fact]
    public async Task JobsSurviveTheV005TableRebuild()
    {
        // V005 rebuilds the jobs table (create-new, copy, drop the old, rename). Against an empty
        // database it copies nothing, so the copy step was effectively untested.
        await CreateSchemaAsOfAsync("V004__pull_request");

        var jobId = Guid.NewGuid();
        await InsertMinimalRowAsync("jobs", jobId);

        await using var provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // Still present, and still carrying its identity — a rebuild that silently dropped or
        // re-keyed rows would lose a customer's job definitions.
        Assert.Equal(1L, Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM jobs;")));
        Assert.Equal(
            jobId.ToString(),
            Convert.ToString(await Scalar(_dbPath, "SELECT id FROM jobs;")),
            ignoreCase: true);
    }

    [Fact]
    public async Task TheV011IndexSwap_SurvivesRowsThatAlreadyExist()
    {
        // V011 de-duplicates object_states and replaces a BINARY unique index with a NOCASE one.
        // Both steps are no-ops on an empty table; on a populated one the DELETE and the index
        // rebuild are where an upgrade would fail or lose rows.
        await CreateSchemaAsOfAsync("V010__perf_indexes");

        for (var i = 0; i < 3; i++)
        {
            await InsertMinimalRowAsync("object_states", Guid.NewGuid(), i);
        }

        await using var provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // Distinct rows must all survive; V011 only removes case-collisions.
        Assert.Equal(3L, Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM object_states;")));
    }

    [Fact]
    public async Task TheV011Deduplication_CollapsesCaseTwinsAndKeepsTheLatest()
    {
        // What V011 is FOR. The old BINARY unique index let a case-only rename (dbo.Foo -> dbo.FOO)
        // insert a second row instead of updating the first, and the new NOCASE index cannot be
        // created while both exist — so the DELETE has to find them. On an empty table it never
        // ran, which meant the upgrade path for every database that had actually hit the bug was
        // the one path with no coverage.
        await CreateSchemaAsOfAsync("V010__perf_indexes");

        await InsertMinimalRowAsync("object_states", Guid.NewGuid(), ordinal: 0);
        await InsertMinimalRowAsync("object_states", Guid.NewGuid(), ordinal: 1);

        // Force the two rows into a case-only collision on the identity tuple.
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await Exec(connection, "UPDATE object_states SET job_id = 'J', database_name = 'SalesDB', "
                + "object_type = 'StoredProcedure', schema_name = 'dbo';");
            await Exec(connection, "UPDATE object_states SET object_name = 'Foo' WHERE id = (SELECT MIN(id) FROM object_states);");
            await Exec(connection, "UPDATE object_states SET object_name = 'FOO' WHERE id = (SELECT MAX(id) FROM object_states);");
        }

        await using var provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // Exactly one survives, and it is the LATER one — upserts update in place, so the higher id
        // is the more recent truth.
        Assert.Equal(1L, Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM object_states;")));
        Assert.Equal("FOO", Convert.ToString(await Scalar(_dbPath, "SELECT object_name FROM object_states;")));
    }

    [Fact]
    public async Task MigratingIsIdempotent_SoASecondStartChangesNothing()
    {
        // Both the service and the app run this at startup, back to back, after every upgrade.
        await CreateSchemaAsOfAsync("V001__init");

        await using var provider = BuildProvider();
        var initializer = provider.GetRequiredService<IDatabaseInitializer>();
        await initializer.InitializeAsync();
        var first = Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM __migrations;"));

        await initializer.InitializeAsync();

        Assert.Equal(first, Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM __migrations;")));
    }

    [Fact]
    public async Task TwoInitializersRacing_BothSucceed()
    {
        // The MSI starts the service and the finish page launches the app, so two processes can
        // migrate the same file within the same second. The design that makes this safe — BEGIN
        // IMMEDIATE plus a re-check of the applied set under the write lock — had no test at all.
        await CreateSchemaAsOfAsync("V001__init");

        await using var a = BuildProvider();
        await using var b = BuildProvider();

        await Task.WhenAll(
            a.GetRequiredService<IDatabaseInitializer>().InitializeAsync(),
            b.GetRequiredService<IDatabaseInitializer>().InitializeAsync());

        // Applied exactly once each, despite both racing to apply them.
        Assert.Equal(
            (long)AllMigrations().Count,
            Convert.ToInt64(await Scalar(_dbPath, "SELECT COUNT(*) FROM __migrations;")));
    }

    /// <summary>
    /// Inserts one valid row into <paramref name="table"/>, supplying only the columns SQLite
    /// actually requires.
    /// </summary>
    /// <remarks>
    /// Driven by the declared types and NOT NULL flags in <c>PRAGMA table_info</c> rather than by
    /// column names. An earlier version of this fixture guessed values from names and broke on the
    /// first NOT NULL column it had not anticipated; the subject of the test is the migration, so
    /// the seed data must not need maintaining every time the old schema is touched. Columns that
    /// are nullable or carry a default are omitted entirely.
    /// </remarks>
    private async Task InsertMinimalRowAsync(string table, Guid id, int ordinal = 0)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var columns = new List<(string Name, string Literal)>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                var declaredType = reader.GetString(2);
                var notNull = reader.GetInt32(3) == 1;
                var hasDefault = !await reader.IsDBNullAsync(4);
                var isPrimaryKey = reader.GetInt32(5) > 0;

                // Not every table keys on a GUID: object_states uses an INTEGER rowid, which V011
                // relies on when it keeps MAX(id) of a set of case-twins. Supplying a GUID string
                // there is a datatype mismatch, so let SQLite assign it.
                if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    if (declaredType.Contains("INT", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    columns.Add((name, $"'{id}'"));
                }
                else if ((notNull || isPrimaryKey) && !hasDefault)
                {
                    columns.Add((name, LiteralForType(declaredType, name, ordinal)));
                }
            }
        }

        await Exec(
            connection,
            $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => $"\"{c.Name}\""))}) "
            + $"VALUES ({string.Join(", ", columns.Select(c => c.Literal))});");
    }

    /// <summary>
    /// A value SQLite will accept for a column of this declared type. Text values vary by
    /// <paramref name="ordinal"/> so several seeded rows stay distinct under a unique index.
    /// </summary>
    private static string LiteralForType(string declaredType, string column, int ordinal)
    {
        var type = declaredType.ToUpperInvariant();

        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return "0";
        }

        if (type.Contains("REAL", StringComparison.Ordinal)
            || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal))
        {
            return "0.0";
        }

        if (type.Contains("BLOB", StringComparison.Ordinal))
        {
            return "x''";
        }

        // TEXT, and anything with no declared affinity. JSON columns follow a naming convention in
        // this schema and must hold parseable JSON, because later migrations and the repositories
        // read them back.
        return column.EndsWith("_json", StringComparison.OrdinalIgnoreCase)
            ? "'[]'"
            : $"'{column}{ordinal}'";
    }
}
