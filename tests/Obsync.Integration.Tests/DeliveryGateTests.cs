using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.Alerting;
using Obsync.Git;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.DependencyInjection;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Results;
using Obsync.Shared.Scripting;

namespace Obsync.Integration.Tests;

/// <summary>
/// The delivered-gate contract, exercised through the REAL engine against a real state database
/// (only SQL scripting and git are faked): tracked object state — hashes, deletions, and
/// incremental watermarks — may only advance once this run's changeset is durably delivered.
/// Advancing it after a failed commit (direct mode) or a failed push / pull request (PR mode,
/// whose head branch is recut every run) makes every later run report "no changes" while the
/// repository silently misses the work — the silent-divergence launch blocker this locks down.
/// </summary>
public sealed class DeliveryGateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obsync-delivery-{Guid.NewGuid():N}");

    private ServiceProvider _provider = null!;
    private IGitWorkspace _gitWorkspace = null!;
    private IGitHubService _gitHub = null!;
    private FakeScriptProvider _scripts = null!;
    private SyncJob _job = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _gitWorkspace = Substitute.For<IGitWorkspace>();
        _gitWorkspace.PrepareAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gitWorkspace.HasUnpushedCommitsAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _gitHub = Substitute.For<IGitHubService>();
        _scripts = new FakeScriptProvider();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddObsyncData(Path.Combine(_root, "state.db"));
        services.AddObsyncShared();
        services.AddSingleton<IObjectScriptProvider>(_scripts);
        services.AddSingleton(_gitWorkspace);
        services.AddSingleton(_gitHub);
        services.AddSingleton(Substitute.For<IServerObjectScriptProvider>());
        services.AddSingleton(Substitute.For<ISqlServerProbe>());
        services.AddSingleton(Substitute.For<IDatabaseArtifactReader>());
        services.AddSingleton(Substitute.For<IDatabaseDocumentationReader>());
        services.AddSingleton(Substitute.For<ISecurityAnalysisReader>());
        services.AddSingleton(Substitute.For<IReferenceDataReader>());
        services.AddSingleton(Substitute.For<IModifiedObjectReader>());
        services.AddSingleton(Substitute.For<IRunAlertService>());
        services.AddSingleton<ICredentialStore>(new FakeCredentialStore());
        services.Configure<ObsyncEngineOptions>(o => o.WorkspacesRoot = Path.Combine(_root, "workspaces"));
        services.AddSingleton<ISyncEngine, SyncEngine>();

        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var connection = new SqlConnectionProfile { Name = "c", ServerName = "srv" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        var repository = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n", DefaultBranch = "main" };
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repository);

        _job = new SyncJob
        {
            Name = "delivery-gate",
            ConnectionProfileId = connection.Id,
            RepositoryProfileId = repository.Id,
            Databases = ["Db1"],
            Branch = "main",
            DestinationFolder = "db",
            Selection = new ObjectSelectionProfile
            {
                Preset = ObjectSelectionPreset.Custom,
                CustomTypes = [SqlObjectType.StoredProcedure],
                IncludeObjectInventory = false,
                IncludeDatabaseOptions = false,
                IncludeDatabasePermissionsFile = false,
                IncludeDocumentation = false,
                IncludeSecurityReview = false,
            },
        };
        _job.Advanced.IncrementalScripting = false;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return Task.CompletedTask;
    }

    private Task<SyncRun> RunAsync() =>
        _provider.GetRequiredService<ISyncEngine>().RunJobAsync(_job.Id, RunTrigger.Manual);

    private Task<IReadOnlyList<TrackedObjectState>> StatesAsync() =>
        _provider.GetRequiredService<IObjectStateRepository>().GetForJobDatabaseAsync(_job.Id, "Db1");

    private void SetCommit(bool succeeds) =>
        _gitWorkspace.CommitAllAsync(
                Arg.Any<GitWorkspaceContext>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(succeeds
                ? GitCommitResult.Committed("abc1234def5678")
                : GitCommitResult.Failed("git commit failed: disk full"));

    private void SetPush(bool succeeds) =>
        _gitWorkspace.PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(succeeds ? Result.Success() : Result.Failure("git push failed: could not resolve host github.com"));

    private void SetPullRequest(bool succeeds) =>
        _gitHub.CreatePullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(succeeds
                ? Result.Success(new PullRequestInfo(7, "https://github.com/o/n/pull/7", null))
                : Result.Failure<PullRequestInfo>("GitHub rejected the pull request: token lacks pull-request scope."));

    [Fact]
    public async Task DirectMode_PushFailure_StillAdvancesState_TheCommitIsDurable()
    {
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: false);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Warning, run.Status);
        Assert.Contains("push", run.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var states = await StatesAsync();
        Assert.Single(states); // the local commit is preserved and re-pushed next run
    }

    [Fact]
    public async Task DirectMode_CommitFailure_DoesNotAdvanceState()
    {
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: false);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Empty(await StatesAsync()); // next run re-detects and re-commits the change
    }

    [Fact]
    public async Task PrMode_PushFailure_DoesNotAdvanceState()
    {
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: false);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Warning, run.Status);
        Assert.Empty(await StatesAsync()); // the head branch is recut next run — nothing was delivered
    }

    [Fact]
    public async Task PrMode_PullRequestFailure_DoesNotAdvanceState()
    {
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: false);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Warning, run.Status);
        Assert.Empty(await StatesAsync()); // no PR was opened, so the change never became mergeable
    }

    [Fact]
    public async Task PrMode_Success_AdvancesState()
    {
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("https://github.com/o/n/pull/7", run.PullRequestUrl);
        Assert.Single(await StatesAsync());
    }

    [Fact]
    public async Task DroppedObject_DeletionIsRetained_UntilDelivered()
    {
        // Run 1 commits the object; run 2 (object gone) fails its commit — the deletion must NOT
        // be forgotten; run 3 delivers it and only then may the state row disappear.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);
        Assert.Single(await StatesAsync());

        _scripts.Items = [];
        SetCommit(succeeds: false);
        Assert.Equal(RunStatus.Failed, (await RunAsync()).Status);
        Assert.Single(await StatesAsync()); // deletion NOT persisted — the commit never happened

        SetCommit(succeeds: true);
        var delivered = await RunAsync();
        Assert.Equal(RunStatus.Succeeded, delivered.Status);
        Assert.Equal(1, delivered.ObjectsDeleted); // re-detected on the retry
        Assert.Empty(await StatesAsync());
    }

    private static RawScriptedObject Proc(string name, string body) =>
        RawScriptedObject.Scripted(
            new ScriptedObjectIdentity(SqlObjectType.StoredProcedure, "dbo", name),
            $"CREATE PROCEDURE dbo.{name} AS BEGIN /* {body} */ SELECT 1; END");

    private sealed class FakeScriptProvider : IObjectScriptProvider
    {
        public IReadOnlyList<RawScriptedObject> Items { get; set; } = [];

        public ScriptingStrategy Strategy => ScriptingStrategy.Metadata;

        public async IAsyncEnumerable<RawScriptedObject> ScriptAsync(
            ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in Items.Where(i => request.Types.Contains(i.Identity.Type)))
            {
                yield return item;
            }
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public void Store(string key, string secret) { }
        public string? Retrieve(string key) => "fake-token";
        public void Delete(string key) { }
        public bool Exists(string key) => true;
    }
}
