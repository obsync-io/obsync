using NSubstitute;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Job configuration export/import: round-trips config, re-attaches profiles by name, reports
/// missing profiles with actionable errors, de-duplicates names, and never embeds secrets.
/// </summary>
public sealed class JobConfigPorterTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _repositoryId = Guid.NewGuid();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IConnectionProfileRepository _connections = Substitute.For<IConnectionProfileRepository>();
    private readonly IRepositoryProfileRepository _repositories = Substitute.For<IRepositoryProfileRepository>();
    private readonly List<SyncJob> _saved = [];

    private JobConfigPorter BuildPorter(
        IReadOnlyList<SqlConnectionProfile>? connections = null,
        IReadOnlyList<GitRepositoryProfile>? repositories = null,
        IReadOnlyList<SyncJob>? existingJobs = null)
    {
        var connection = new SqlConnectionProfile { Id = _connectionId, Name = "Prod", ServerName = "PROD-SQL01" };
        var repository = new GitRepositoryProfile
        {
            Id = _repositoryId, Name = "History", Owner = "corp", RepositoryName = "sql-history", DefaultBranch = "main",
        };

        _connections.GetAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(connection);
        _connections.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(connections ?? (IReadOnlyList<SqlConnectionProfile>)[connection]));
        _repositories.GetAsync(_repositoryId, Arg.Any<CancellationToken>()).Returns(repository);
        _repositories.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(repositories ?? (IReadOnlyList<GitRepositoryProfile>)[repository]));
        _jobs.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(existingJobs ?? (IReadOnlyList<SyncJob>)[]));
        _jobs.UpsertAsync(Arg.Do<SyncJob>(_saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        return new JobConfigPorter(_jobs, _connections, _repositories, clock, Substitute.For<IAuditWriter>());
    }

    private SyncJob SampleJob() => new()
    {
        Name = "Sales Sync",
        Description = "nightly",
        ConnectionProfileId = _connectionId,
        RepositoryProfileId = _repositoryId,
        DatabaseScope = DatabaseScope.AllUserDatabases,
        ExcludedDatabases = ["Scratch"],
        Branch = "main",
        DestinationFolder = "environments/prod",
        Tags = ["prod"],
        Selection = new ObjectSelectionProfile { ReferenceDataTables = ["dbo.Currency"] },
        Advanced = new JobAdvancedOptions { SqlRetryCount = 7, ReferenceDataMaxRows = 250 },
    };

    [Fact]
    public async Task ExportThenImport_RoundTripsTheConfiguration()
    {
        var porter = BuildPorter();
        var json = await porter.ExportAsync(SampleJob());

        var result = await porter.ImportAsync(json);

        Assert.True(result.IsSuccess, result.Error);
        var imported = Assert.Single(_saved);
        Assert.Equal("Sales Sync", imported.Name);
        Assert.Equal("nightly", imported.Description);
        Assert.Equal(_connectionId, imported.ConnectionProfileId);   // re-attached by name
        Assert.Equal(_repositoryId, imported.RepositoryProfileId);
        Assert.Equal(DatabaseScope.AllUserDatabases, imported.DatabaseScope);
        Assert.Equal(["Scratch"], imported.ExcludedDatabases);
        Assert.Equal(["dbo.Currency"], imported.Selection.ReferenceDataTables);
        Assert.Equal(7, imported.Advanced.SqlRetryCount);
        Assert.Equal(250, imported.Advanced.ReferenceDataMaxRows);
        Assert.Equal(["prod"], imported.Tags);
    }

    [Fact]
    public async Task Export_ReferencesProfilesByName_AndNeverEmbedsIds()
    {
        var porter = BuildPorter();
        var job = SampleJob();

        var json = await porter.ExportAsync(job);

        Assert.Contains("PROD-SQL01", json);
        Assert.Contains("sql-history", json);
        // Machine-local identifiers must not travel: profiles re-attach by name on import.
        Assert.DoesNotContain(job.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_connectionId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_repositoryId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_MissingServerProfile_FailsWithAnActionableError()
    {
        var porter = BuildPorter(connections: []);
        var json = await porter.ExportAsync(SampleJob());

        var result = await porter.ImportAsync(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("Servers", result.Error);
        Assert.Contains("PROD-SQL01", result.Error);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Import_MissingRepositoryProfile_FailsWithAnActionableError()
    {
        var porter = BuildPorter(repositories: []);
        var json = await porter.ExportAsync(SampleJob());

        var result = await porter.ImportAsync(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("corp/sql-history", result.Error);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Import_ExportOnlyJob_NeedsNoRepositoryProfile()
    {
        var porter = BuildPorter(repositories: []);
        var job = SampleJob();
        job.CommitMode = CommitMode.ExportOnly;
        job.RepositoryProfileId = null;
        job.ExportPath = @"D:\exports";
        var json = await porter.ExportAsync(job);

        var result = await porter.ImportAsync(json);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Null(_saved.Single().RepositoryProfileId);
        Assert.Equal(@"D:\exports", _saved.Single().ExportPath);
    }

    [Fact]
    public async Task Import_NameCollision_AppendsAnImportedSuffix()
    {
        var porter = BuildPorter(existingJobs: [new SyncJob { Name = "Sales Sync" }]);
        var json = await porter.ExportAsync(SampleJob());

        var result = await porter.ImportAsync(json);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Sales Sync (imported)", _saved.Single().Name);
    }

    [Fact]
    public async Task Import_GarbageJson_FailsGracefully()
    {
        var porter = BuildPorter();

        var result = await porter.ImportAsync("this is not json");

        Assert.False(result.IsSuccess);
        Assert.Contains("not a valid Obsync job export", result.Error);
    }
}
