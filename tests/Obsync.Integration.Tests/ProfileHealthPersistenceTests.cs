using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>
/// Exercises the V013 columns against a real SQLite database: the structured server edition/version
/// captured at test time on connection profiles, and the persisted validation outcome on repository
/// profiles — via both the full upsert and the status-only update methods.
/// </summary>
public sealed class ProfileHealthPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-health-test-{Guid.NewGuid():N}.db");
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
    public async Task ConnectionProfile_ServerInfo_RoundTripsThroughUpsert()
    {
        var connections = _provider.GetRequiredService<IConnectionProfileRepository>();
        var profile = new SqlConnectionProfile
        {
            Name = "Prod",
            ServerName = "PROD-SQL01",
            ServerEdition = "Enterprise Edition",
            ServerVersion = "16.0.4105.2",
        };

        await connections.UpsertAsync(profile);
        var loaded = await connections.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Enterprise Edition", loaded!.ServerEdition);
        Assert.Equal("16.0.4105.2", loaded.ServerVersion);
        Assert.Equal("Enterprise Edition · 16.0.4105.2", loaded.ServerProductDisplay);
    }

    [Fact]
    public async Task ConnectionProfile_UpdateTestStatus_PersistsTheStructuredServerInfo()
    {
        var connections = _provider.GetRequiredService<IConnectionProfileRepository>();
        var profile = new SqlConnectionProfile { Name = "Prod", ServerName = "PROD-SQL01" };
        await connections.UpsertAsync(profile);

        var testedAt = new DateTimeOffset(2026, 7, 16, 9, 30, 0, TimeSpan.Zero);
        await connections.UpdateTestStatusAsync(
            profile.Id, ConnectionTestStatus.Connected, testedAt,
            "SQL Server Standard Edition (15.0.2000.5)", "Standard Edition", "15.0.2000.5");

        var loaded = await connections.GetAsync(profile.Id);
        Assert.Equal(ConnectionTestStatus.Connected, loaded!.LastTestStatus);
        Assert.Equal(testedAt, loaded.LastTestedAt);
        Assert.Equal("Standard Edition", loaded.ServerEdition);
        Assert.Equal("15.0.2000.5", loaded.ServerVersion);
    }

    [Fact]
    public async Task RepositoryProfile_ValidationOutcome_RoundTripsThroughUpsert()
    {
        var repositories = _provider.GetRequiredService<IRepositoryProfileRepository>();
        var validatedAt = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var profile = new GitRepositoryProfile
        {
            Name = "schema-history",
            Owner = "company",
            RepositoryName = "sql-schema-history",
            LastValidationStatus = RepositoryValidationStatus.Attention,
            LastValidatedAt = validatedAt,
            LastValidationDetail = "Read-only access — Direct Commit and Pull Request jobs will fail to push.",
        };

        await repositories.UpsertAsync(profile);
        var loaded = await repositories.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(RepositoryValidationStatus.Attention, loaded!.LastValidationStatus);
        Assert.Equal(validatedAt, loaded.LastValidatedAt);
        Assert.Equal(profile.LastValidationDetail, loaded.LastValidationDetail);
    }

    [Fact]
    public async Task RepositoryProfile_NeverValidated_LoadsAsUnvalidated()
    {
        var repositories = _provider.GetRequiredService<IRepositoryProfileRepository>();
        var profile = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" };

        await repositories.UpsertAsync(profile);
        var loaded = await repositories.GetAsync(profile.Id);

        Assert.Equal(RepositoryValidationStatus.Unvalidated, loaded!.LastValidationStatus);
        Assert.Null(loaded.LastValidatedAt);
        Assert.Null(loaded.LastValidationDetail);
    }

    [Fact]
    public async Task RepositoryProfile_UpdateValidationStatus_RoundTripsWithoutTouchingTheRest()
    {
        var repositories = _provider.GetRequiredService<IRepositoryProfileRepository>();
        var profile = new GitRepositoryProfile
        {
            Name = "schema-history", Owner = "company", RepositoryName = "sql-schema-history", DefaultBranch = "develop",
        };
        await repositories.UpsertAsync(profile);

        var validatedAt = new DateTimeOffset(2026, 7, 16, 11, 0, 0, TimeSpan.Zero);
        await repositories.UpdateValidationStatusAsync(
            profile.Id, RepositoryValidationStatus.Failed, validatedAt,
            "Branch 'develop' not found in company/sql-schema-history.");

        var loaded = await repositories.GetAsync(profile.Id);
        Assert.Equal(RepositoryValidationStatus.Failed, loaded!.LastValidationStatus);
        Assert.Equal(validatedAt, loaded.LastValidatedAt);
        Assert.Equal("Branch 'develop' not found in company/sql-schema-history.", loaded.LastValidationDetail);
        Assert.Equal("schema-history", loaded.Name);        // the rest of the profile is untouched
        Assert.Equal("develop", loaded.DefaultBranch);
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
