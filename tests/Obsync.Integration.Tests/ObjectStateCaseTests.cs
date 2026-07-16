using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Integration.Tests;

/// <summary>
/// SQL Server object identities are case-insensitive; the tracked-state identity must be too.
/// Before V011 a case-only rename (dbo.Foo → dbo.FOO) inserted a SECOND row, and every later run
/// failed loading the prior map with a duplicate-identity error.
/// </summary>
public sealed class ObjectStateCaseTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obsync-nocase-{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;
    private Guid _jobId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(Path.Combine(_root, "state.db"));
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var connection = new SqlConnectionProfile { Name = "c", ServerName = "srv" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        var job = new SyncJob { Name = "case", ConnectionProfileId = connection.Id, Databases = ["Db1"] };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);
        _jobId = job.Id;
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CaseOnlyRename_UpdatesTheExistingRow_AndKeepsTheNewCasing()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        await states.UpsertAsync(State("dbo", "Foo", "hash-1"));
        await states.UpsertAsync(State("dbo", "FOO", "hash-2"));

        var rows = await states.GetForJobDatabaseAsync(_jobId, "Db1");
        var row = Assert.Single(rows);
        Assert.Equal("FOO", row.ObjectName);
        Assert.Equal("hash-2", row.LastHash);
    }

    [Fact]
    public async Task DatabaseNameLookup_IsCaseInsensitive()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        await states.UpsertAsync(State("dbo", "Foo", "hash-1"));

        Assert.Single(await states.GetForJobDatabaseAsync(_jobId, "DB1"));
    }

    private TrackedObjectState State(string schema, string name, string hash) => new()
    {
        JobId = _jobId,
        DatabaseName = "Db1",
        ObjectType = SqlObjectType.StoredProcedure,
        SchemaName = schema,
        ObjectName = name,
        FilePath = $"db/procedures/{schema}.{name}.sql",
        LastHash = hash,
        LastScriptedAt = DateTimeOffset.UtcNow,
        LastStatus = RunStatus.Succeeded,
    };
}
