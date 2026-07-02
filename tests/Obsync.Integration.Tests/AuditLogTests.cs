using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>
/// Exercises the V003 audit trail end to end against a real SQLite database: the audit_log table
/// (write → read) and the runs.triggered_by attribution column.
/// </summary>
public sealed class AuditLogTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-audit-test-{Guid.NewGuid():N}.db");
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

    private AuditWriter CreateWriter() =>
        new(_provider.GetRequiredService<IDbConnectionFactory>(), SystemClock.Instance);

    [Fact]
    public async Task AuditEvents_WriteThenReadBack_NewestFirstWithActorAndAction()
    {
        var writer = CreateWriter();
        var jobId = Guid.NewGuid();

        await writer.WriteAsync(AuditAction.JobCreated, "Job", jobId.ToString(), "SalesDB Sync");
        await writer.WriteAsync(AuditAction.ServerDeleted, "Server", Guid.NewGuid().ToString(), "PROD-SQL01");

        var events = await writer.GetRecentAsync();

        Assert.Equal(2, events.Count);
        // Newest first: the server deletion was written last.
        Assert.Equal(AuditAction.ServerDeleted, events[0].Action);
        Assert.Equal(AuditAction.JobCreated, events[1].Action);

        var created = events[1];
        Assert.Equal("Job", created.EntityType);
        Assert.Equal(jobId.ToString(), created.EntityId);
        Assert.Equal("SalesDB Sync", created.EntityName);
        Assert.False(string.IsNullOrWhiteSpace(created.Actor)); // captured from the current identity
        Assert.NotEqual(default, created.OccurredAt);
    }

    [Fact]
    public async Task GetRecent_HonoursTheLimit()
    {
        var writer = CreateWriter();
        for (var i = 0; i < 5; i++)
        {
            await writer.WriteAsync(AuditAction.JobEdited, "Job", Guid.NewGuid().ToString(), $"Job {i}");
        }

        var events = await writer.GetRecentAsync(limit: 3);

        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task Run_TriggeredBy_RoundTrips()
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
            RunKey = "20260702-090000",
            JobId = job.Id,
            JobName = job.Name,
            Status = RunStatus.Running,
            ServerName = "PROD-SQL01",
            Databases = "SalesDB",
            TriggeredBy = @"CONTOSO\alice",
            StartedAt = DateTimeOffset.UtcNow,
        };
        await runs.InsertAsync(run);

        var reloaded = await runs.GetAsync(run.Id);

        Assert.Equal(@"CONTOSO\alice", reloaded!.TriggeredBy);
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
