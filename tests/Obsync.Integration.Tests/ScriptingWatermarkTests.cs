using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Integration.Tests;

/// <summary>
/// Incremental-scripting watermarks round-trip through the repository (V009), including the
/// upsert-updates-in-place rule, verbatim datetime storage, and the job-delete cascade.
/// </summary>
public sealed class ScriptingWatermarkTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(_dbPath);
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<SyncJob> InsertJobAsync(string name)
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        var job = new SyncJob { Name = name, ConnectionProfileId = connection.Id, CommitMode = CommitMode.ExportOnly, ExportPath = "x" };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);
        return job;
    }

    private static ScriptingWatermark Mark(DateTime value, string? fingerprint = "fp-current") =>
        new(value, fingerprint);

    /// <summary>
    /// A watermark carries the emission fingerprint that produced the hashes it vouches for, and a
    /// row written before the column existed reads back null — which the engine adopts rather than
    /// invalidating, so upgrading does not force a full scan of every type.
    /// </summary>
    [Fact]
    public async Task AWatermark_CarriesTheFingerprintThatProducedIt()
    {
        var job = await InsertJobAsync("fp");
        var watermarks = _provider.GetRequiredService<IScriptingWatermarkRepository>();
        var mark = new DateTime(2026, 9, 1, 8, 30, 0);

        await watermarks.UpsertManyAsync(job.Id, "db", new Dictionary<SqlObjectType, ScriptingWatermark>
        {
            [SqlObjectType.View] = Mark(mark, "abc123"),
            [SqlObjectType.Table] = Mark(mark, fingerprint: null),
        });

        var loaded = await watermarks.GetForJobDatabaseAsync(job.Id, "db");
        Assert.Equal("abc123", loaded[SqlObjectType.View].Fingerprint);
        Assert.Null(loaded[SqlObjectType.Table].Fingerprint);

        // Re-upserting replaces the fingerprint in place, so a run under a new configuration
        // re-stamps rather than leaving the old claim behind.
        await watermarks.UpsertManyAsync(job.Id, "db", new Dictionary<SqlObjectType, ScriptingWatermark>
        {
            [SqlObjectType.View] = Mark(mark, "def456"),
        });
        loaded = await watermarks.GetForJobDatabaseAsync(job.Id, "db");
        Assert.Equal("def456", loaded[SqlObjectType.View].Fingerprint);
    }

    [Fact]
    public async Task Watermarks_RoundTripVerbatim_AndUpsertUpdatesInPlace()
    {
        var job = await InsertJobAsync("j");
        var watermarks = _provider.GetRequiredService<IScriptingWatermarkRepository>();

        // Server-local datetimes with sub-second precision must round-trip exactly, unshifted.
        var tableMark = new DateTime(2026, 7, 5, 23, 14, 59, 997);
        var procMark = new DateTime(2026, 7, 5, 22, 0, 0);
        await watermarks.UpsertManyAsync(job.Id, "SalesDB", new Dictionary<SqlObjectType, ScriptingWatermark>
        {
            [SqlObjectType.Table] = Mark(tableMark),
            [SqlObjectType.StoredProcedure] = Mark(procMark),
        });
        await watermarks.UpsertManyAsync(job.Id, "OtherDB", new Dictionary<SqlObjectType, ScriptingWatermark>
        {
            [SqlObjectType.Table] = Mark(tableMark.AddDays(-1)),
        });

        var loaded = await watermarks.GetForJobDatabaseAsync(job.Id, "SalesDB");
        Assert.Equal(2, loaded.Count);
        Assert.Equal(tableMark, loaded[SqlObjectType.Table].Value);
        Assert.Equal(procMark, loaded[SqlObjectType.StoredProcedure].Value);

        // Re-upserting advances in place (PK job/database/type) — no duplicate rows.
        var advanced = tableMark.AddHours(1);
        await watermarks.UpsertManyAsync(job.Id, "SalesDB", new Dictionary<SqlObjectType, ScriptingWatermark>
        {
            [SqlObjectType.Table] = Mark(advanced),
        });

        loaded = await watermarks.GetForJobDatabaseAsync(job.Id, "SalesDB");
        Assert.Equal(2, loaded.Count);
        Assert.Equal(advanced, loaded[SqlObjectType.Table].Value);
        Assert.Equal(procMark, loaded[SqlObjectType.StoredProcedure].Value);

        // The other database's rows are untouched, and an unknown database reads back empty.
        Assert.Single(await watermarks.GetForJobDatabaseAsync(job.Id, "OtherDB"));
        Assert.Empty(await watermarks.GetForJobDatabaseAsync(job.Id, "NoSuchDB"));
    }

    [Fact]
    public async Task DeletingTheJob_CascadesItsWatermarksAway()
    {
        var job = await InsertJobAsync("doomed");
        var survivor = await InsertJobAsync("survivor");
        var watermarks = _provider.GetRequiredService<IScriptingWatermarkRepository>();
        var mark = new DateTime(2026, 7, 5, 12, 0, 0);
        await watermarks.UpsertManyAsync(job.Id, "db", new Dictionary<SqlObjectType, ScriptingWatermark> { [SqlObjectType.View] = Mark(mark) });
        await watermarks.UpsertManyAsync(survivor.Id, "db", new Dictionary<SqlObjectType, ScriptingWatermark> { [SqlObjectType.View] = Mark(mark) });

        await _provider.GetRequiredService<IJobRepository>().DeleteAsync(job.Id);

        Assert.Empty(await watermarks.GetForJobDatabaseAsync(job.Id, "db"));
        Assert.Single(await watermarks.GetForJobDatabaseAsync(survivor.Id, "db"));
    }

    public void Dispose()
    {
        _provider?.Dispose();
        TestDatabase.ReleasePool(_dbPath);
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
