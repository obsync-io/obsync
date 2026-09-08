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

    private static RawScriptedObject View(string name, string body) =>
        RawScriptedObject.Scripted(
            new ScriptedObjectIdentity(SqlObjectType.View, "dbo", name),
            $"CREATE VIEW dbo.{name} AS SELECT 1 AS c; /* {body} */");

    private sealed class FakeScriptProvider : IObjectScriptProvider
    {
        public IReadOnlyList<RawScriptedObject> Items { get; set; } = [];

        /// <summary>
        /// Each item's <c>modify_date</c>, keyed by "type|schema|name", so the incremental floor can
        /// be applied the way a real provider applies it.
        /// </summary>
        public Dictionary<string, DateTime> ModifyDates { get; } = [];

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
                // It then went too far the other way and dropped the WHOLE type, which no real
                // provider does — every one of them filters on `modify_date >= watermark`
                // (MetadataScriptProvider.AppendWatermarkFilter), so an object at or above the
                // floor is still yielded. Dropping the type wholesale invented deletions for those
                // boundary objects and made a genuine bug look worse than it is. An item with no
                // recorded date is yielded, matching a provider that has nothing to filter on.
                if (request.IncrementalWatermarks?.TryGetValue(item.Identity.Type, out var floor) == true
                    && ModifyDates.TryGetValue(
                        $"{(int)item.Identity.Type}|{item.Identity.Schema}|{item.Identity.Name}", out var modifiedAt)
                    && modifiedAt < floor)
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
        SetSnapshotOf([.. objects.Select(o => (SqlObjectType.StoredProcedure, o.Name, o.ModifyDate))]);

    /// <summary>A modification snapshot spanning more than one object type.</summary>
    private void SetSnapshotOf(params (SqlObjectType Type, string Name, DateTime ModifyDate)[] objects)
    {
        // The provider filters on the same dates the snapshot reports, exactly as SQL Server does —
        // they are two reads of one `modify_date`, so a fixture where they disagree tests nothing
        // that can happen.
        foreach (var o in objects)
        {
            _scripts.ModifyDates[$"{(int)o.Type}|dbo|{o.Name}"] = o.ModifyDate;
        }

        _provider.GetRequiredService<IModifiedObjectReader>()
            .GetSnapshotAsync(
                Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<SqlObjectType>>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ModifiedObjectSnapshotItem>>(
                [.. objects.Select(o => new ModifiedObjectSnapshotItem(
                    o.Type, "dbo", o.Name, o.ModifyDate))]));
    }

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

    [Fact]
    public async Task PrMode_ADeletionInAClosedUnmergedPullRequest_IsReproposed()
    {
        // A deletion has no state row left by construction — dropping the row IS how a delivered
        // deletion was recorded. So if the reviewer closes the pull request unmerged, the object is
        // gone from SQL, the file is still on base, and nothing connects them: the file is orphaned
        // in the customer's repository permanently and the deletion is never proposed again.
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);
        _ = await RunAsync();
        var file = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));

        // The object is dropped in SQL. Run 2 proposes the deletion in a pull request.
        _scripts.Items = [];
        var proposing = await RunAsync();
        Assert.Equal(1, proposing.ObjectsDeleted);
        Assert.False(File.Exists(file));

        // The reviewer closes it unmerged: base still carries the file, so the recut tree has it back.
        await File.WriteAllTextAsync(file, "CREATE PROCEDURE dbo.P1 AS BEGIN /* body v1 */ SELECT 1; END");

        var recovery = await RunAsync();

        // It must be proposed again rather than forgotten.
        Assert.Equal(1, recovery.ObjectsDeleted);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task PrMode_ADeletionThatMerged_RetiresQuietly()
    {
        // The other half, and the one that stops the tombstone becoming an every-run empty commit:
        // once the deletion lands, the recut tree no longer carries the file, so the row retires
        // with nothing reported.
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);
        _ = await RunAsync();

        _scripts.Items = [];
        _ = await RunAsync();          // propose the deletion

        // The pull request merges: the file is gone from base and stays gone.
        var settled = await RunAsync();

        Assert.Equal(0, settled.ObjectsDeleted);
        Assert.Equal(RunStatus.NoChanges, settled.Status);
        Assert.Empty(await StatesAsync());   // the tombstone retired
    }

    [Fact]
    public async Task DirectMode_ADeletion_StillDropsItsStateImmediately()
    {
        // Direct mode's delivery is real: the push lands on the branch it tracks, so there is
        // nothing to keep a tombstone for.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        _ = await RunAsync();

        _scripts.Items = [];
        var deleting = await RunAsync();

        Assert.Equal(1, deleting.ObjectsDeleted);
        Assert.Empty(await StatesAsync());
    }

    [Fact]
    public async Task PrMode_ADeletionWhoseFileIsAlreadyGone_IsNotReportedAsADeletion()
    {
        // Pull-request mode only. Its tree is recut from base every run, so an absent file really
        // does mean base no longer has it — reporting a deletion then would inflate the counts and
        // produce an empty commit. Direct mode must NOT do this: there, an absent file can simply
        // mean an earlier run deleted it locally and failed to commit, and retiring on that would
        // orphan the file on the remote.
        _job.CommitMode = CommitMode.PullRequest;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);
        _ = await RunAsync();
        var file = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));

        // Someone removes both the object and the file by hand.
        _scripts.Items = [];
        File.Delete(file);

        var run = await RunAsync();

        Assert.Equal(0, run.ObjectsDeleted);
        Assert.Empty(await StatesAsync());
    }
    [Fact]
    public async Task ACrlfWorkingTree_IsNotMistakenForEveryObjectHavingChanged()
    {
        // The regression this reproduces was reported from production as "13,305 modified" on a run
        // where nothing in SQL had changed.
        //
        // Obsync writes LF. The bundled MinGit ships core.autocrlf=true, and the git hardening
        // deliberately does not override it, so git converts LF to CRLF on checkout. After any clone
        // or re-clone every file on disk is byte-different from what was scripted even though the
        // blob git stores is identical -- so a raw byte comparison reported the entire estate as
        // modified, rewrote all of it, and produced no commit at all.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        _ = await RunAsync();

        var file = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));

        // Exactly what a checkout leaves behind: identical content, CRLF endings.
        var asWritten = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("\r\n", asWritten);
        await File.WriteAllTextAsync(file, asWritten.Replace("\n", "\r\n"));

        var second = await RunAsync();

        Assert.Equal(RunStatus.NoChanges, second.Status);
        Assert.Equal(0, second.ObjectsModified);

        // And the file is left alone rather than needlessly rewritten.
        Assert.Contains("\r\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task AGenuineContentChange_IsStillDetectedInACrlfTree()
    {
        // The fix must not blind the comparison: normalizing line endings must not normalize away a
        // real difference that happens to arrive alongside them.
        //
        // Counted as RESTORED rather than Modified, and that is the right word: the scripted
        // definition never changed, so nothing about the object was modified. What differed was the
        // file in the repository, which Obsync overwrites because SQL Server is the source of truth.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        _ = await RunAsync();

        var file = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));

        // CRLF endings AND different content -- the scripted body must still win.
        await File.WriteAllTextAsync(file, "CREATE PROCEDURE dbo.P1 AS\r\nBEGIN /* body v0 */ SELECT 1; END\r\n");

        var second = await RunAsync();

        Assert.Equal(0, second.ObjectsModified);
        Assert.Equal(1, second.ObjectsRestored);
        Assert.Contains("body v1", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task PrMode_AnUnmergedPullRequest_ReproposesEveryObject_NotSomeOfThem()
    {
        // The recut is the whole point: pull-request mode does `checkout -B <head> origin/<base>`
        // every run, so while the pull request sits unmerged, base does not carry the proposal and
        // every file it proposed is ABSENT from the working tree.
        //
        // DivergedTypes used to treat absence as harmless, reasoning that "the self-heal already
        // rewrites those". That is false for a planner-skipped object: a planned skip writes
        // nothing at all and never reaches the self-heal. So the incremental filter dropped exactly
        // the objects the recut had deleted, and the run committed a PARTIAL tree. Merging that
        // partial proposal would land a fragment on base with every state row still saying
        // "delivered", and nothing would ever re-propose the rest.
        //
        // Two objects for the same reason as the divergence test above: P2 sets the watermark and
        // P1 falls behind it, so P1 is the one the planner can skip.
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

        var p1 = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("P1", StringComparison.OrdinalIgnoreCase));

        // The recut, exactly: base has no such file, so the checkout removes it.
        File.Delete(p1);

        var recovery = await RunAsync();

        Assert.True(File.Exists(p1), "the recut-deleted object was skipped and never re-proposed");
        Assert.Contains("body v1", await File.ReadAllTextAsync(p1));
        Assert.NotEqual(RunStatus.NoChanges, recovery.Status);
    }
    [Fact]
    public async Task AnUnchangedObjectWhoseFileIsMissing_CountsAsRestored_NotModified()
    {
        // The complaint that produced this: a run where nothing in SQL had changed reported the
        // entire estate — 13,305 objects — as Modified.
        //
        // The hash comparison is the authority on whether an OBJECT changed, and for every one of
        // them it said Unchanged. The engine then relabelled each as Modified purely so its file
        // would be rewritten after the recut deleted it. Two unrelated facts under one word:
        // the definition changed, and the repository needed catching up.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        var first = await RunAsync();
        Assert.Equal(1, first.ObjectsAdded);

        // Exactly what the recut leaves behind while a pull request sits unmerged: base does not
        // carry the file, so the checkout removes it.
        File.Delete(TrackedFile());

        var second = await RunAsync();

        Assert.Equal(0, second.ObjectsModified);
        Assert.Equal(1, second.ObjectsRestored);
        Assert.Equal(0, second.ObjectsAdded);
        Assert.Equal(0, second.ObjectsDeleted);

        // Still delivered, and still counted as something the commit contains — the run must not
        // decide it has nothing to do and skip the commit entirely.
        Assert.Equal(1, second.ChangeCount);
        Assert.Contains("body v1", await File.ReadAllTextAsync(TrackedFile()));
    }

    [Fact]
    public async Task AnObjectWhoseDefinitionChanged_StillCountsAsModified()
    {
        // The other half: separating restoration from modification must not stop a real
        // modification being reported as one.
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        _ = await RunAsync();

        _scripts.Items = [Proc("P1", "body v2")];

        var second = await RunAsync();

        Assert.Equal(1, second.ObjectsModified);
        Assert.Equal(0, second.ObjectsRestored);
    }

    [Fact]
    public async Task TheObjectInventoryArtifact_IsCountedAsScanned()
    {
        // The inventory is written by its own code path rather than through ApplyItemAsync, and it
        // was the one item counted as a change but never as scanned — so the tiles read one lower
        // on the left than on the right, which is what "13,304 scanned / 13,305 modified" was.
        _job.Selection.IncludeObjectInventory = true;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
        _scripts.Items = [Proc("P1", "body v1")];
        SetCommit(succeeds: true);
        SetPush(succeeds: true);

        var run = await RunAsync();

        // One stored procedure plus the inventory artifact, on both sides of the tiles.
        Assert.Equal(2, run.ObjectsScanned);
        Assert.Equal(2, run.ObjectsAdded + run.ObjectsModified + run.ObjectsRestored);
    }

    [Fact]
    public async Task WhenOneTypeDiverges_TheOtherTypesFilterIsNotAppliedToIt()
    {
        // A type withheld for divergence must be scanned in FULL — that is the entire purpose of
        // withholding it. Instead its watermark leaked back out and was handed to the provider as a
        // filter, so the type the engine had just decided to re-scan was filtered HARDER than
        // normal, and the objects it filtered away were never marked seen. The deletion pass then
        // read every one of them as dropped.
        //
        // The leak: FilterableTypes is derived from the STORED watermark keys, and a diverged type
        // still has its row — watermark writes are upserts and never delete. The returned dictionary
        // was intersected with FilterableTypes but not with the types actually being scanned.
        //
        // Partial divergence is the reachable shape: after a merge, base carries everything, then
        // one type gets a change proposed and left unmerged. Its files on base hold the old content
        // and it alone diverges. Two objects per type because the skip rule needs a modify_date
        // strictly below the type's watermark, and the watermark is the snapshot's max.
        var older = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var newer = older.AddHours(1);

        _job.Advanced.IncrementalScripting = true;
        _job.CommitMode = CommitMode.PullRequest;
        _job.Selection.CustomTypes = [SqlObjectType.StoredProcedure, SqlObjectType.View];
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);

        _scripts.Items = [Proc("P1", "p one"), Proc("P2", "p two"), View("V1", "v one"), View("V2", "v two")];
        SetSnapshotOf(
            (SqlObjectType.StoredProcedure, "P1", older), (SqlObjectType.StoredProcedure, "P2", newer),
            (SqlObjectType.View, "V1", older), (SqlObjectType.View, "V2", newer));
        SetCommit(succeeds: true);
        SetPush(succeeds: true);
        SetPullRequest(succeeds: true);

        _ = await RunAsync();   // deliver all four; both types get a watermark of `newer`
        _ = await RunAsync();   // steady state

        // Only the VIEW type diverges: its file on base still holds what the reviewer has not
        // merged. The procedures are untouched, so they stay filterable.
        var v1 = Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Contains("V1", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(v1, "-- still what base carries");

        var recovery = await RunAsync();

        // The views must be re-scanned, not deleted.
        Assert.Equal(0, recovery.ObjectsDeleted);
        Assert.Equal(4, (await StatesAsync()).Count);
        Assert.Contains("v one", await File.ReadAllTextAsync(v1));
    }
}
