using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Integration.Tests;

/// <summary>
/// The batch writers (object-state upserts, run changes, run logs) issue multi-row INSERT chunks —
/// one command per chunk instead of one per row. These tests prove the chosen chunk sizes against
/// the actual bundled e_sqlite3 (a full chunk is a single statement carrying ~2,800 parameters),
/// that the NOCASE upsert semantics survived the rewrite, and that V012 dropped the redundant
/// run_changes index.
/// </summary>
public sealed class BatchInsertChunkingTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-chunk-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private SyncJob _job = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(_dbPath);
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        var repo = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);
        _job = new SyncJob { Name = "j", ConnectionProfileId = connection.Id, RepositoryProfileId = repo.Id };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BundledSqlite_VariableLimit_CoversTheLargestChunkParameterBudget()
    {
        // The chunk sizes assume the bundled e_sqlite3 accepts at least a full upsert chunk's
        // parameters per statement (200 rows × 14 = 2,800). Pin the actual compiled limit so a
        // future native-library downgrade (e.g. to a 999-variable build) fails loudly here rather
        // than at a customer's first VLDB run.
        await using var connection = await _provider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        var handle = connection.Handle;
        Assert.NotNull(handle);
        var limit = SQLitePCL.raw.sqlite3_limit(handle, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, -1);
        Assert.True(
            limit >= ObjectStateRepository.UpsertChunkRows * 14,
            $"SQLITE_LIMIT_VARIABLE_NUMBER is {limit}, below the full upsert chunk's parameter count.");
    }

    [Fact]
    public async Task UpsertMany_SpanningChunkBoundaries_RoundTripsAndUpdatesInPlace()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        var total = (ObjectStateRepository.UpsertChunkRows * 3) + 7; // three full chunks + a remainder

        await states.UpsertManyAsync([.. Enumerable.Range(0, total).Select(i => State(i, $"usp_{i:D4}", $"hash{i}"))]);

        var loaded = await states.GetForJobDatabaseAsync(_job.Id, "Db1");
        Assert.Equal(total, loaded.Count);
        var first = loaded.Single(s => s.ObjectName == "usp_0000");
        Assert.Equal("hash0", first.LastHash);
        Assert.Equal("procedures/dbo.usp_0000.sql", first.FilePath);
        Assert.Equal($"hash{total - 1}", loaded.Single(s => s.ObjectName == $"usp_{total - 1:D4}").LastHash);

        // Upsert the same identities with new hashes, one as a case twin (usp_0000 → USP_0000):
        // rows must update in place (NOCASE identity) and keep the new casing.
        await states.UpsertManyAsync(
            [.. Enumerable.Range(0, total).Select(i => State(i, i == 0 ? "USP_0000" : $"usp_{i:D4}", $"new{i}"))]);

        loaded = await states.GetForJobDatabaseAsync(_job.Id, "Db1");
        Assert.Equal(total, loaded.Count);
        var twin = loaded.Single(s => string.Equals(s.ObjectName, "usp_0000", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("USP_0000", twin.ObjectName);
        Assert.Equal("new0", twin.LastHash);
        Assert.Equal($"new{total - 1}", loaded.Single(s => s.ObjectName == $"usp_{total - 1:D4}").LastHash);
    }

    [Fact]
    public async Task AddChanges_SpanningAChunkBoundary_RoundTrips()
    {
        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = await InsertRunAsync(runs);
        var total = RunRepository.ChangeChunkRows + 25;

        await runs.AddChangesAsync(run.Id, [.. Enumerable.Range(0, total).Select(i => new ObjectChange
        {
            ChangeType = ChangeType.Added,
            ObjectType = SqlObjectType.StoredProcedure,
            Schema = "dbo",
            Name = $"usp_{i:D4}",
            RelativePath = $"procedures/dbo.usp_{i:D4}.sql",
            NewHash = $"h{i:D4}",
        })]);

        var changes = await runs.GetChangesAsync(run.Id);
        Assert.Equal(total, changes.Count);
        Assert.Equal("usp_0000", changes[0].Name); // ordered by (change_type, schema, name)
        Assert.Equal("h0000", changes[0].NewHash);
        Assert.Equal($"usp_{total - 1:D4}", changes[^1].Name);
        Assert.Equal($"h{total - 1:D4}", changes[^1].NewHash);
    }

    [Fact]
    public async Task AddLogs_SpanningAChunkBoundary_RoundTrips()
    {
        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = await InsertRunAsync(runs);
        var total = RunRepository.LogChunkRows + 13;
        var timestamp = DateTimeOffset.UtcNow;

        await runs.AddLogsAsync([.. Enumerable.Range(0, total).Select(i => new SyncRunLog
        {
            RunId = run.Id, Timestamp = timestamp, Message = $"message {i}",
        })]);

        var logs = await runs.GetLogsAsync(run.Id);
        Assert.Equal(total, logs.Count);
        Assert.Equal("message 0", logs[0].Message); // ordered by id = insertion order
        Assert.Equal($"message {total - 1}", logs[^1].Message);
    }

    [Fact]
    public async Task V012_DroppedTheRedundantRunChangesIndex()
    {
        // ix_run_changes_run_id (V001) is a strict prefix of ix_run_changes_run_order (V010);
        // V012 drops it so run_changes inserts maintain one index, not two.
        await using var connection = await _provider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'run_changes';";
        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.DoesNotContain("ix_run_changes_run_id", indexes);
        Assert.Contains("ix_run_changes_run_order", indexes);
    }

    private async Task<SyncRun> InsertRunAsync(IRunRepository runs)
    {
        var run = new SyncRun
        {
            RunKey = "20260716-120000", JobId = _job.Id, JobName = _job.Name, Status = RunStatus.Running,
            ServerName = "s", Databases = "Db1", StartedAt = DateTimeOffset.UtcNow,
        };
        await runs.InsertAsync(run);
        return run;
    }

    private TrackedObjectState State(int i, string name, string hash) => new()
    {
        JobId = _job.Id,
        DatabaseName = "Db1",
        ObjectType = SqlObjectType.StoredProcedure,
        SchemaName = "dbo",
        ObjectName = name,
        FilePath = $"procedures/dbo.usp_{i:D4}.sql",
        LastHash = hash,
        LastScriptedAt = DateTimeOffset.UtcNow,
        LastStatus = RunStatus.Succeeded,
    };

    public void Dispose()
    {
        _provider?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp database file.
        }
    }
}
