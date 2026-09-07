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
        var unsupported = Substitute.For<IUnsupportedObjectReader>();
        unsupported.ReadAsync(
                Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UnsupportedObjectGroup>>([]));
        services.AddSingleton(unsupported);
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
    public async Task DirectMode_PushFailure_DoesNotAdvanceState()
    {
        // This test previously asserted the opposite, on the reasoning that a local commit is
        // durable delivery because the next run re-pushes a stranded one. The re-push is real — but
        // the durability is conditional, and the condition is that the clone survives. Three
        // ordinary events destroy it: the corrupt-workspace self-heal re-clones from scratch,
        // changing the workspaces root in Settings abandons the old location, and a backup restore
        // or antivirus can remove the directory outright.
        //
        // After any of those, with state already advanced, every affected object's stored hash
        // matches content that was never delivered — so the job reports NoChanges forever against a
        // repository that never received the work, and the run row asserts a commit SHA that only
        // ever existed on one machine. Direct mode now waits for the push, exactly as PR mode below
        // waits for the pull request. Nothing is lost by waiting: the stranded commit is still
        // re-pushed on the next run, and state advances then.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: false);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Warning, run.Status);
        Assert.Contains("push", run.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await StatesAsync());
    }

    [Fact]
    public async Task DirectMode_AStrandedCommit_IsDeliveredByTheNextRun()
    {
        // The other half of the rule above: not advancing state must not lose the work. The second
        // run re-scripts the same content, git stages nothing because the tree already matches, and
        // the branch is still ahead of origin — so it pushes the stranded commit and only then
        // records delivery.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: false);
        Assert.Empty(await StatesAsync());
        _ = await RunAsync();

        SetPush(succeeds: true);
        var second = await RunAsync();

        Assert.NotEqual(RunStatus.Failed, second.Status);
        Assert.Single(await StatesAsync());
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
                // Honour the incremental floor, as a real provider does. This fake used to ignore it
                // entirely, which quietly made every incremental test meaningless: objects reached
                // the engine whether or not the planner had filtered them out, so nothing could
                // observe the difference between "filtered" and "not filtered".
                //
                // A filtered type yields NOTHING here, which is the true production shape for this
                // scenario — the object was delivered, its watermark advanced past it, and SQL has
                // not changed since. That is exactly how a skipped object never reaches the engine's
                // self-heal.
                if (request.IncrementalWatermarks?.ContainsKey(item.Identity.Type) == true)
                {
                    continue;
                }

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

    /// <summary>The single scripted object's file, wherever the layout put it.</summary>
    private string TrackedFile() =>
        Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task PrMode_AClosedUnmergedPullRequest_ReproposesTheModification()
    {
        // In PR mode "delivered" means PROPOSED, not landed. If the reviewer closes the pull request
        // without merging, base keeps the OLD file while state records the NEW hash as delivered.
        // Both self-heal tests then passed — the hash matched, and the file existed — so the object
        // was never written again and the modification was silently dropped from every later run.
        //
        // An enterprise GitHub that requires approval to merge makes this the NORMAL state.
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);

        var first = await RunAsync();
        Assert.NotEqual(RunStatus.Failed, first.Status);
        Assert.Single(await StatesAsync());

        // The pull request is closed unmerged: the tree reverts to what base still holds.
        await File.WriteAllTextAsync(TrackedFile(), "body v0 -- still what base carries");

        var second = await RunAsync();

        Assert.NotEqual(RunStatus.NoChanges, second.Status);
        Assert.Contains("body v1", await File.ReadAllTextAsync(TrackedFile()));
    }

    [Fact]
    public async Task PrMode_AMatchingTree_IsStillNoChanges()
    {
        // The other half, and the one that stops this becoming a rewrite-everything-every-run
        // regression: when the tree already carries exactly what state says was delivered, there is
        // nothing to do.
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);
        _ = await RunAsync();

        Assert.Equal(RunStatus.NoChanges, (await RunAsync()).Status);
    }

    [Fact]
    public async Task DirectMode_AMatchingTree_IsStillNoChanges()
    {
        // Direct mode pushes to the branch it tracks, so delivery means landed and the tree cannot
        // legitimately disagree. It must not pay for a divergence it cannot have.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        _ = await RunAsync();

        Assert.Equal(RunStatus.NoChanges, (await RunAsync()).Status);
    }

    [Fact]
    public async Task PrMode_AClosedUnmergedPullRequest_ReproposesTheModification_WithIncrementalScriptingOn()
    {
        // The configuration that actually ships: IncrementalScripting defaults to TRUE, and it
        // filters at the PROVIDER — so a skipped object never reaches the self-heal at all. The
        // content check alone would therefore have fixed nothing for a real job. This asserts the
        // outcome in the default configuration rather than the one the fixture happens to use.
        _job.Advanced.IncrementalScripting = true;
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);

        var first = await RunAsync();
        Assert.NotEqual(RunStatus.Failed, first.Status);

        await File.WriteAllTextAsync(TrackedFile(), "body v0 -- still what base carries");

        var second = await RunAsync();

        Assert.NotEqual(RunStatus.NoChanges, second.Status);
        Assert.Contains("body v1", await File.ReadAllTextAsync(TrackedFile()));
    }


    /// <summary>
    /// Makes the modification snapshot real, so the incremental planner actually engages.
    /// </summary>
    /// <remarks>
    /// The fixture substitutes IModifiedObjectReader with a no-op, so GetSnapshotAsync returned
    /// nothing and the planner had nothing to skip — which is why every attempt to observe the
    /// incremental path here silently tested nothing.
    /// </remarks>
    private void SetSnapshot(params (string Name, DateTime ModifyDate)[] objects) =>
        _provider.GetRequiredService<IModifiedObjectReader>()
            .GetSnapshotAsync(
                Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<SqlObjectType>>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ModifiedObjectSnapshotItem>>(
                [.. objects.Select(o => new ModifiedObjectSnapshotItem(
                    SqlObjectType.StoredProcedure, "dbo", o.Name, o.ModifyDate))]));

    [Fact]
    public async Task PrMode_AnIncrementallySkippedObject_IsStillReproposedWhenTheTreeDiverged()
    {
        // The case the whole of Phase 2 turns on, and the one no earlier test could reach.
        //
        // A planned skip writes NOTHING — it marks the object seen, counts it scanned, and returns
        // — so the self-heal that compares file content never runs for it. An object whose pull
        // request was closed unmerged has both an advanced hash and an advanced watermark, so it is
        // skipped on every later run and its modification is lost in silence.
        //
        // The skip rule needs a modify_date STRICTLY older than the type's watermark, and the
        // watermark is the snapshot's max — so two objects are required: P2 sets the watermark, and
        // P1 falls behind it and is therefore the one that gets skipped.
        var older = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var newer = older.AddHours(1);

        _job.Advanced.IncrementalScripting = true;
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1"), Proc("P2", "other")];
        SetSnapshot(("P1", older), ("P2", newer));
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);

        _ = await RunAsync();   // deliver both; watermark for StoredProcedure becomes `newer`
        _ = await RunAsync();   // P1 is now skippable (older < newer)

        // The reviewer closes P1's pull request unmerged: base still carries the old file.
        var p1 = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(p1, "body v0 -- still what base carries");

        var recovery = await RunAsync();

        Assert.NotEqual(RunStatus.NoChanges, recovery.Status);
        Assert.Contains("body v1", await File.ReadAllTextAsync(p1));
    }
}
