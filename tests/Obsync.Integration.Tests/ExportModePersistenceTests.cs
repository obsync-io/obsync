using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>
/// Exercises the V005 export modes against a real SQLite database: an Export Only job with a NULL
/// repository (proving the table-rebuild made repository_profile_id nullable) plus export_path, and a
/// git-mode job that still carries its repository.
/// </summary>
public sealed class ExportModePersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-export-test-{Guid.NewGuid():N}.db");
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
    public async Task ExportOnlyJob_WithNullRepository_RoundTrips()
    {
        var connectionId = await SeedConnectionAsync();
        var jobs = _provider.GetRequiredService<IJobRepository>();

        var job = new SyncJob
        {
            Name = "Air-gap export",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = null, // no GitHub repo — only possible after the V005 nullable rebuild
            CommitMode = CommitMode.ExportOnly,
            ExportPath = @"D:\exports\SalesDB.zip",
            Databases = ["SalesDB"],
        };
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.RepositoryProfileId);
        Assert.Equal(CommitMode.ExportOnly, loaded.CommitMode);
        Assert.Equal(@"D:\exports\SalesDB.zip", loaded.ExportPath);
    }

    [Fact]
    public async Task GitModeJob_StillStoresItsRepository()
    {
        var connectionId = await SeedConnectionAsync();
        var repo = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" };
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);
        var jobs = _provider.GetRequiredService<IJobRepository>();

        var job = new SyncJob
        {
            Name = "Local commit",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repo.Id,
            CommitMode = CommitMode.LocalCommitOnly,
            Databases = ["SalesDB"],
        };
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.Equal(repo.Id, loaded!.RepositoryProfileId);
        Assert.Equal(CommitMode.LocalCommitOnly, loaded.CommitMode);
        Assert.Null(loaded.ExportPath);
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
            // Best-effort cleanup.
        }
    }
}
