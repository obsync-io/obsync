using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>
/// Exercises the V004 pull-request columns end to end against a real SQLite database:
/// jobs.reviewers_json and runs.pr_url / pr_number (including the run UPDATE path the engine uses
/// when it opens the PR during finalize).
/// </summary>
public sealed class PullRequestPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-pr-test-{Guid.NewGuid():N}.db");
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

    private async Task<(Guid connId, Guid repoId)> SeedProfilesAsync()
    {
        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        var repo = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);
        return (connection.Id, repo.Id);
    }

    [Fact]
    public async Task Job_Reviewers_RoundTrip()
    {
        var (connId, repoId) = await SeedProfilesAsync();
        var jobs = _provider.GetRequiredService<IJobRepository>();

        var job = new SyncJob
        {
            Name = "PR job",
            ConnectionProfileId = connId,
            RepositoryProfileId = repoId,
            CommitMode = CommitMode.PullRequest,
            Reviewers = ["alice", "bob"],
        };
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal(CommitMode.PullRequest, loaded!.CommitMode);
        Assert.Equal(["alice", "bob"], loaded.Reviewers);
    }

    [Fact]
    public async Task Job_WithNoReviewers_LoadsAsEmptyList()
    {
        var (connId, repoId) = await SeedProfilesAsync();
        var jobs = _provider.GetRequiredService<IJobRepository>();
        var job = new SyncJob { Name = "direct", ConnectionProfileId = connId, RepositoryProfileId = repoId };
        await jobs.UpsertAsync(job);

        var loaded = await jobs.GetAsync(job.Id);

        Assert.NotNull(loaded!.Reviewers);
        Assert.Empty(loaded.Reviewers);
    }

    [Fact]
    public async Task Run_PullRequestFields_PersistThroughUpdate()
    {
        var (connId, repoId) = await SeedProfilesAsync();
        var job = new SyncJob { Name = "j", ConnectionProfileId = connId, RepositoryProfileId = repoId };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = new SyncRun
        {
            RunKey = "20260702-230000",
            JobId = job.Id,
            JobName = job.Name,
            Status = RunStatus.Running,
            ServerName = "PROD-SQL01",
            Databases = "SalesDB",
            StartedAt = DateTimeOffset.UtcNow,
        };
        await runs.InsertAsync(run); // inserted before the PR exists

        // The engine opens the PR during finalize, then writes the run's final state via UpdateAsync.
        run.Status = RunStatus.Succeeded;
        run.PullRequestUrl = "https://github.com/o/n/pull/42";
        run.PullRequestNumber = 42;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await runs.UpdateAsync(run);

        var reloaded = await runs.GetAsync(run.Id);

        Assert.Equal("https://github.com/o/n/pull/42", reloaded!.PullRequestUrl);
        Assert.Equal(42, reloaded.PullRequestNumber);
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
