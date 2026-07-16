using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Integration.Tests;

/// <summary>The Dependencies-tab picker queries: capped LIKE search and per-job database list.</summary>
public sealed class ObjectStateSearchTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-objsearch-test-{Guid.NewGuid():N}.db");
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

        var states = _provider.GetRequiredService<IObjectStateRepository>();
        await states.UpsertManyAsync(
        [
            NewState("SalesDB", SqlObjectType.Table, "dbo", "Customers"),
            NewState("SalesDB", SqlObjectType.Table, "dbo", "CustomerOrders"),
            NewState("SalesDB", SqlObjectType.View, "reporting", "vw_Customers"),
            NewState("SalesDB", SqlObjectType.StoredProcedure, "dbo", "usp_GetOrder"),
            NewState("CRM", SqlObjectType.Table, "dbo", "Leads"),
            // Synthetic artifact rows must never appear in the picker or database list.
            NewState("SalesDB", SqlObjectType.DatabaseArtifact, "$database", "object-inventory"),
            NewState("$server", (SqlObjectType)70, "$server", "logins"),
        ]);
    }

    private TrackedObjectState NewState(string database, SqlObjectType type, string schema, string name) => new()
    {
        JobId = _job.Id,
        DatabaseName = database,
        ObjectType = type,
        SchemaName = schema,
        ObjectName = name,
        FilePath = $"{schema}/{name}.sql",
        LastHash = "hash",
        LastScriptedAt = DateTimeOffset.UtcNow,
    };

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Search_MatchesNameOrSchemaQualifiedName_CaseInsensitively()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();

        var byName = await states.SearchAsync(_job.Id, "SalesDB", "customer", 50);
        Assert.Equal(3, byName.Count); // Customers, CustomerOrders, vw_Customers

        var bySchemaQualified = await states.SearchAsync(_job.Id, "SalesDB", "reporting.vw", 50);
        var match = Assert.Single(bySchemaQualified);
        Assert.Equal("vw_Customers", match.ObjectName);
    }

    [Fact]
    public async Task Search_EmptyQueryListsAlphabetically_AndHonoursTheCap()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();

        var all = await states.SearchAsync(_job.Id, "SalesDB", string.Empty, 50);
        Assert.Equal(4, all.Count); // synthetic artifact excluded
        Assert.Equal("CustomerOrders", all[0].ObjectName);

        var capped = await states.SearchAsync(_job.Id, "SalesDB", string.Empty, 2);
        Assert.Equal(2, capped.Count);
    }

    [Fact]
    public async Task Search_MatchesTheDatabaseNameCaseInsensitively()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();

        // The identity index compares database_name with NOCASE (V011); a differently-cased
        // database name must find the same rows, consistent with GetForJobDatabaseAsync.
        var all = await states.SearchAsync(_job.Id, "SALESDB", string.Empty, 50);
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task Search_EscapesLikeWildcards()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();

        // '%' must not act as a match-everything wildcard when the user types it.
        Assert.Empty(await states.SearchAsync(_job.Id, "SalesDB", "%", 50));
        // '_' must be literal too: "usp_Get" matches usp_GetOrder, not arbitrary characters.
        var underscore = await states.SearchAsync(_job.Id, "SalesDB", "usp_Get", 50);
        Assert.Single(underscore);
    }

    [Fact]
    public async Task DatabaseList_IsDistinctSorted_AndExcludesSyntheticRows()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();

        var databases = await states.GetDatabasesForJobAsync(_job.Id);

        Assert.Equal(["CRM", "SalesDB"], databases);
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
