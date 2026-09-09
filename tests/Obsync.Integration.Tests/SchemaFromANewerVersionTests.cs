using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Obsync.Data;
using Obsync.Data.DependencyInjection;

namespace Obsync.Integration.Tests;

/// <summary>
/// An older build must refuse a database written by a newer one.
/// </summary>
/// <remarks>
/// The initializer only ever asked "have I applied this migration?", so a <c>__migrations</c> row
/// for a version it had never heard of was invisible: everything it knew was already applied,
/// nothing was pending, and it started successfully against a schema from the future without so
/// much as a log line.
///
/// <para>
/// Uninstalling is the way in. The MSI's downgrade block matches installed products sharing the
/// UpgradeCode, but an uninstall deregisters the product first — so uninstall-newer,
/// install-older walks straight past it, and the data root survives both because it lives outside
/// the install folder. Additive migrations happen to be survivable; a rebuilt table is not, and two
/// of the shipped migrations rebuild one. There is no pre-migration backup either.
/// </para>
/// </remarks>
public sealed class SchemaFromANewerVersionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-newer-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.ReleasePool(_dbPath);
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddObsyncData(_dbPath);
        return services.BuildServiceProvider();
    }

    private async Task StampAsync(params string[] versions)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS __migrations (version TEXT NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();

        foreach (var version in versions)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO __migrations (version, applied_at) VALUES ($v, $t);";
            insert.Parameters.AddWithValue("$v", version);
            insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ADatabaseHoldingAnUnknownMigration_IsRefused()
    {
        // What a future release would leave behind.
        await StampAsync("V001__init", "V999__from_the_future");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await using var provider = BuildProvider();
                await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
            });

        // The message has to be actionable on its own: it is all a user gets, in a startup dialog or
        // an event-log entry.
        Assert.Contains("newer version", error.Message);
        Assert.Contains("V999__from_the_future", error.Message);
        Assert.Contains("data folder", error.Message);
    }

    [Fact]
    public async Task EveryUnknownMigration_IsNamed()
    {
        // Naming only the first would leave support guessing how far ahead the database is.
        await StampAsync("V001__init", "V900__a", "V901__b");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await using var provider = BuildProvider();
                await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
            });

        Assert.Contains("V900__a", error.Message);
        Assert.Contains("V901__b", error.Message);
    }

    [Fact]
    public async Task AFreshDatabase_IsNotMistakenForANewerOne()
    {
        // The guard must not fire on the overwhelmingly common case of no __migrations rows at all.
        await using var provider = BuildProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __migrations;";
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0);
    }

    [Fact]
    public async Task ADatabaseAtTheCurrentSchema_StartsNormally()
    {
        // And so must the ordinary restart case, where every known migration is already applied.
        await using (var first = BuildProvider())
        {
            await first.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        await using var second = BuildProvider();
        await second.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // "An old-but-known schema still migrates forward" is deliberately NOT tested here. Stamping
    // __migrations without executing the migration SQL produces a database whose rows claim a schema
    // its tables do not have, which tests the fixture rather than the guard. That case is covered
    // properly by MigrationFromPopulatedSchemaTests, which builds each old schema by running the
    // real migrations against it.
}
