using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>
/// Exercises the V007 environment-tag columns end to end against a real SQLite database:
/// jobs.tags_json and the denormalized runs.tags_json stamped at run start.
/// </summary>
public sealed class EnvironmentTagsPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-tags-test-{Guid.NewGuid():N}.db");
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

    private async Task<Guid> SeedConnectionAsync()
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        return connection.Id;
    }

    [Fact]
    public async Task Job_Tags_RoundTrip()
    {
        var connId = await SeedConnectionAsync();
        var jobs = _provider.GetRequiredService<IJobRepository>();

        var job = new SyncJob
        {
            Name = "Prod Sync",
            ConnectionProfileId = connId,
            CommitMode = CommitMode.ExportOnly,
            ExportPath = @"C:\export",
            Tags = ["prod", "finance"],
        };
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.Equal(["prod", "finance"], loaded!.Tags);
    }

    [Fact]
    public async Task Job_WithNoTags_LoadsAsEmptyList()
    {
        var connId = await SeedConnectionAsync();
        var jobs = _provider.GetRequiredService<IJobRepository>();
        var job = new SyncJob { Name = "untagged", ConnectionProfileId = connId, CommitMode = CommitMode.ExportOnly, ExportPath = "x" };
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.NotNull(loaded!.Tags);
        Assert.Empty(loaded.Tags);
    }

    [Fact]
    public async Task Run_Tags_PersistAndReload()
    {
        var connId = await SeedConnectionAsync();
        var job = new SyncJob { Name = "j", ConnectionProfileId = connId, CommitMode = CommitMode.ExportOnly, ExportPath = "x" };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = new SyncRun
        {
            RunKey = "20260702-230000",
            JobId = job.Id,
            JobName = job.Name,
            Status = RunStatus.Succeeded,
            ServerName = "PROD-SQL01",
            Databases = "SalesDB",
            StartedAt = DateTimeOffset.UtcNow,
            Tags = ["prod", "pci"],
        };
        await runs.InsertAsync(run);

        var reloaded = await runs.GetAsync(run.Id);
        Assert.Equal(["prod", "pci"], reloaded!.Tags);

        // Also surfaces through the list queries History uses.
        var recent = await runs.GetForJobAsync(job.Id);
        Assert.Equal(["prod", "pci"], recent.Single().Tags);
    }

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
