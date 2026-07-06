using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Integration.Tests;

public sealed class DataRoundTripTests : IAsyncLifetime, IDisposable
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

    [Fact]
    public async Task Job_RoundTripsThroughRepositoryWithNestedJson()
    {
        var connection = new SqlConnectionProfile { Name = "PROD-SQL01", ServerName = "PROD-SQL01" };
        var repo = new GitRepositoryProfile { Name = "schema-history", Owner = "company", RepositoryName = "sql-schema-history" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);

        var job = new SyncJob
        {
            Name = "SalesDB Production Sync",
            ConnectionProfileId = connection.Id,
            RepositoryProfileId = repo.Id,
            Databases = ["SalesDB"],
            DestinationFolder = "environments/prod/PROD-SQL01/SalesDB",
            Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.Recommended },
        };
        job.Selection.SchemaFilter.Add("dbo");

        var jobs = _provider.GetRequiredService<IJobRepository>();
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal("SalesDB Production Sync", loaded!.Name);
        Assert.Equal(["SalesDB"], loaded.Databases);
        Assert.Equal(ObjectSelectionPreset.Recommended, loaded.Selection.Preset);
        Assert.Contains("dbo", loaded.Selection.SchemaFilter);
        Assert.Equal(connection.Id, loaded.ConnectionProfileId);
    }

    [Fact]
    public async Task Job_WithDynamicDatabaseScope_RoundTrips()
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);

        var job = new SyncJob
        {
            Name = "All-DB estate sync",
            ConnectionProfileId = connection.Id,
            CommitMode = CommitMode.ExportOnly,
            ExportPath = @"C:\exports",
            DatabaseScope = DatabaseScope.AllUserDatabases,
            Databases = [],
            ExcludedDatabases = ["Scratch", "TempWork"],
            Selection = new ObjectSelectionProfile { ReferenceDataTables = ["dbo.Currency", "ref.Status"] },
            Advanced = new JobAdvancedOptions { ReferenceDataMaxRows = 750 },
        };

        var jobs = _provider.GetRequiredService<IJobRepository>();
        await jobs.UpsertAsync(job);
        var loaded = await jobs.GetAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal(DatabaseScope.AllUserDatabases, loaded!.DatabaseScope);
        Assert.Empty(loaded.Databases);
        Assert.Equal(["Scratch", "TempWork"], loaded.ExcludedDatabases);
        Assert.Equal("All user databases (excl. Scratch, TempWork)", loaded.DatabasesDisplay);
        Assert.Equal(["dbo.Currency", "ref.Status"], loaded.Selection.ReferenceDataTables);
        Assert.Equal(750, loaded.Advanced.ReferenceDataMaxRows);
    }

    [Fact]
    public async Task Run_WithLogsAndChanges_RoundTrips()
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        var repo = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);
        var job = new SyncJob { Name = "j", ConnectionProfileId = connection.Id, RepositoryProfileId = repo.Id };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = new SyncRun
        {
            RunKey = "20260628-230000",
            JobId = job.Id,
            JobName = job.Name,
            Status = RunStatus.Running,
            ServerName = "PROD-SQL01",
            Databases = "SalesDB",
            StartedAt = DateTimeOffset.UtcNow,
        };
        await runs.InsertAsync(run);

        await runs.AddLogsAsync([new SyncRunLog { RunId = run.Id, Timestamp = DateTimeOffset.UtcNow, Message = "Scanned 42,120 objects" }]);
        await runs.AddChangesAsync(run.Id,
        [
            new ObjectChange
            {
                ChangeType = ChangeType.Modified, ObjectType = SqlObjectType.StoredProcedure,
                Schema = "dbo", Name = "usp_GetCustomer", RelativePath = "procedures/dbo.usp_GetCustomer.sql",
            },
        ]);

        run.Status = RunStatus.Succeeded;
        run.ObjectsModified = 1;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await runs.UpdateAsync(run);

        var reloaded = await runs.GetAsync(run.Id);
        var logs = await runs.GetLogsAsync(run.Id);
        var changes = await runs.GetChangesAsync(run.Id);

        Assert.Equal(RunStatus.Succeeded, reloaded!.Status);
        Assert.Equal(1, reloaded.ChangeCount);
        Assert.Single(logs);
        Assert.Equal("Scanned 42,120 objects", logs[0].Message);
        Assert.Single(changes);
        Assert.Equal(ChangeType.Modified, changes[0].ChangeType);
        Assert.Equal("dbo.usp_GetCustomer", changes[0].QualifiedName);
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
