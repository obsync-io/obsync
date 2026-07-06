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

    [Fact]
    public async Task RunRetention_DeletesOldRunsWithTheirChildren_AndKeepsRecentOnes()
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        var job = new SyncJob { Name = "j", ConnectionProfileId = connection.Id, CommitMode = CommitMode.ExportOnly, ExportPath = "x" };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        var runs = _provider.GetRequiredService<IRunRepository>();
        var now = DateTimeOffset.UtcNow;

        var oldRun = new SyncRun
        {
            RunKey = "old", JobId = job.Id, JobName = "j", Status = RunStatus.Succeeded,
            ServerName = "s", Databases = "d", StartedAt = now.AddDays(-100),
        };
        await runs.InsertAsync(oldRun);
        await runs.AddLogsAsync([new SyncRunLog { RunId = oldRun.Id, Timestamp = now.AddDays(-100), Message = "m" }]);

        var recentRun = new SyncRun
        {
            RunKey = "new", JobId = job.Id, JobName = "j", Status = RunStatus.Failed,
            Trigger = RunTrigger.Scheduled,
            ServerName = "s", Databases = "d", StartedAt = now.AddDays(-1),
        };
        await runs.InsertAsync(recentRun);

        var deleted = await runs.DeleteRunsBeforeAsync(now.AddDays(-90));

        Assert.Equal(1, deleted);
        Assert.Null(await runs.GetAsync(oldRun.Id));
        Assert.Empty(await runs.GetLogsAsync(oldRun.Id));      // cascade removed the children
        Assert.NotNull(await runs.GetAsync(recentRun.Id));

        // The startup notification counts unattended failures only (the recent run is a
        // scheduled failure; a manual failure must not count).
        Assert.Equal(1, await runs.CountUnattendedFailuresSinceAsync(now.AddDays(-2)));
        Assert.Equal(0, await runs.CountUnattendedFailuresSinceAsync(now));
    }

    [Fact]
    public async Task AppSettings_DailyDriverOptions_RoundTrip()
    {
        var settings = _provider.GetRequiredService<IAppSettingsRepository>();

        // Defaults first.
        Assert.Equal(0, await settings.GetRunRetentionDaysAsync());
        Assert.Equal(CommitterIdentity.Default, await settings.GetCommitterAsync());
        Assert.True(string.IsNullOrEmpty(await settings.GetWorkspacesRootOverrideAsync()));
        Assert.True(await settings.GetNotifyRunFailuresAsync());
        Assert.Null(await settings.GetLastFailureCheckAsync());

        await settings.SetRunRetentionDaysAsync(90);
        await settings.SetCommitterAsync(new CommitterIdentity("DBA Team", "dba@corp.com"));
        await settings.SetWorkspacesRootOverrideAsync(@"D:\ObsyncWorkspaces");
        await settings.SetNotifyRunFailuresAsync(false);
        var checkedAt = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        await settings.SetLastFailureCheckAsync(checkedAt);

        Assert.Equal(90, await settings.GetRunRetentionDaysAsync());
        Assert.Equal(new CommitterIdentity("DBA Team", "dba@corp.com"), await settings.GetCommitterAsync());
        Assert.Equal(@"D:\ObsyncWorkspaces", await settings.GetWorkspacesRootOverrideAsync());
        Assert.False(await settings.GetNotifyRunFailuresAsync());
        Assert.Equal(checkedAt, await settings.GetLastFailureCheckAsync());

        // Clearing the workspaces override returns to the default.
        await settings.SetWorkspacesRootOverrideAsync(null);
        Assert.True(string.IsNullOrEmpty(await settings.GetWorkspacesRootOverrideAsync()));
    }

    [Fact]
    public async Task ObjectStates_BatchUpsertAndDelete_RoundTrip()
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        var job = new SyncJob { Name = "j", ConnectionProfileId = connection.Id, CommitMode = CommitMode.ExportOnly, ExportPath = "x" };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        var states = _provider.GetRequiredService<IObjectStateRepository>();
        var batch = Enumerable.Range(0, 1500).Select(i => new TrackedObjectState
        {
            JobId = job.Id,
            DatabaseName = "db",
            ObjectType = Shared.Objects.SqlObjectType.StoredProcedure,
            SchemaName = "dbo",
            ObjectName = $"usp_{i}",
            FilePath = $"procedures/dbo.usp_{i}.sql",
            LastHash = $"hash{i}",
            LastScriptedAt = DateTimeOffset.UtcNow,
            LastStatus = RunStatus.Succeeded,
        }).ToList();

        await states.UpsertManyAsync(batch);
        var loaded = await states.GetForJobDatabaseAsync(job.Id, "db");
        Assert.Equal(1500, loaded.Count);

        // Upserting again must UPDATE (unique identity), not duplicate.
        batch[0].LastHash = "changed";
        await states.UpsertManyAsync(batch);
        loaded = await states.GetForJobDatabaseAsync(job.Id, "db");
        Assert.Equal(1500, loaded.Count);
        Assert.Equal("changed", loaded.Single(s => s.ObjectName == "usp_0").LastHash);

        // Batch delete spans multiple 500-id chunks.
        await states.DeleteManyAsync([.. loaded.Take(1200).Select(s => s.Id)]);
        Assert.Equal(300, (await states.GetForJobDatabaseAsync(job.Id, "db")).Count);
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
