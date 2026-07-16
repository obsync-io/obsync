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
/// Deletion-safety contract, exercised through the REAL engine against a real state database:
/// narrowing the job's scope (schema filter, type selection) must retain committed files, a
/// suspicious mass disappearance must suspend deletions on unattended runs, a disabled job must
/// not execute unattended, and PR mode must not push a zero-diff branch. These lock down defects
/// found during the 2026-07 production-readiness audit.
/// </summary>
public sealed class DeletionSafetyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obsync-delsafety-{Guid.NewGuid():N}");

    private ServiceProvider _provider = null!;
    private IGitWorkspace _gitWorkspace = null!;
    private IGitHubService _gitHub = null!;
    private FilteringScriptProvider _scripts = null!;
    private SyncJob _job = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _gitWorkspace = Substitute.For<IGitWorkspace>();
        _gitWorkspace.PrepareAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gitWorkspace.HasUnpushedCommitsAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _gitWorkspace.CommitAllAsync(
                Arg.Any<GitWorkspaceContext>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GitCommitResult.Committed("abc1234def5678"));
        _gitWorkspace.PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gitHub = Substitute.For<IGitHubService>();
        _scripts = new FilteringScriptProvider();

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
            Name = "deletion-safety",
            ConnectionProfileId = connection.Id,
            RepositoryProfileId = repository.Id,
            Databases = ["Db1"],
            Branch = "main",
            DestinationFolder = "db",
            Selection = new ObjectSelectionProfile
            {
                Preset = ObjectSelectionPreset.Custom,
                CustomTypes = [SqlObjectType.StoredProcedure, SqlObjectType.View],
                IncludeObjectInventory = false,
                IncludeDatabaseOptions = false,
                IncludeDatabasePermissionsFile = false,
                IncludeDocumentation = false,
                IncludeSecurityReview = false,
            },
        };
        _job.Advanced.IncrementalScripting = false;
        await SaveJobAsync();
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return Task.CompletedTask;
    }

    private Task SaveJobAsync() => _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);

    private Task<SyncRun> RunAsync(RunTrigger trigger = RunTrigger.Manual) =>
        _provider.GetRequiredService<ISyncEngine>().RunJobAsync(_job.Id, trigger);

    private Task<IReadOnlyList<TrackedObjectState>> StatesAsync() =>
        _provider.GetRequiredService<IObjectStateRepository>().GetForJobDatabaseAsync(_job.Id, "Db1");

    [Fact]
    public async Task NarrowingTheSchemaFilter_RetainsOutOfFilterFiles()
    {
        _job.Selection.CustomTypes.Add(SqlObjectType.Schema);
        await SaveJobAsync();
        _scripts.Items = [Proc("dbo", "P1"), Proc("sales", "P2"), Schema("sales")];
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);
        Assert.Equal(3, (await StatesAsync()).Count);

        // Narrow the filter to dbo: sales.P2 AND the sales schema's own DDL file are out of
        // SCOPE, not dropped — both must stay.
        _job.Selection.SchemaFilter.Add("dbo");
        await SaveJobAsync();
        var run = await RunAsync();

        Assert.Equal(0, run.ObjectsDeleted);
        Assert.Equal(3, (await StatesAsync()).Count);
    }

    [Fact]
    public async Task DeselectingAType_RetainsItsFiles()
    {
        _scripts.Items = [Proc("dbo", "P1"), View("dbo", "V1")];
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);
        Assert.Equal(2, (await StatesAsync()).Count);

        _job.Selection.CustomTypes = [SqlObjectType.StoredProcedure];
        await SaveJobAsync();
        var run = await RunAsync();

        Assert.Equal(0, run.ObjectsDeleted);
        Assert.Equal(2, (await StatesAsync()).Count);
    }

    [Fact]
    public async Task MassDisappearance_SuspendsDeletionsOnScheduledRuns_ManualRunApplies()
    {
        _scripts.Items = [.. Enumerable.Range(1, 120).Select(i => Proc("dbo", $"P{i}"))];
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);
        Assert.Equal(120, (await StatesAsync()).Count);

        // Everything vanishes at once (the signature of lost metadata visibility, e.g. a revoked
        // VIEW DEFINITION). An unattended run must NOT commit the wipe.
        _scripts.Items = [];
        var scheduled = await RunAsync(RunTrigger.Scheduled);
        Assert.Equal(RunStatus.Warning, scheduled.Status);
        Assert.Equal(0, scheduled.ObjectsDeleted);
        Assert.Contains("suspended", scheduled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(120, (await StatesAsync()).Count);

        // A manual Run Now is the explicit confirmation — the deletions apply.
        var manual = await RunAsync(RunTrigger.Manual);
        Assert.Equal(120, manual.ObjectsDeleted);
        Assert.Empty(await StatesAsync());
    }

    [Fact]
    public async Task DisabledJob_ScheduledTriggerDoesNotRun()
    {
        _scripts.Items = [Proc("dbo", "P1")];
        _job.Enabled = false;
        await SaveJobAsync();

        var run = await RunAsync(RunTrigger.Scheduled);

        Assert.Equal(RunStatus.NoChanges, run.Status);
        Assert.Equal(0, run.ObjectsScanned);
        Assert.Empty(await _provider.GetRequiredService<IRunRepository>().GetForJobAsync(_job.Id));

        // The user's explicit Run Now stays allowed on a disabled job.
        var manual = await RunAsync(RunTrigger.Manual);
        Assert.Equal(RunStatus.Succeeded, manual.Status);
    }

    [Fact]
    public async Task PrMode_IdenticalTree_DoesNotPushOrOpenAPullRequest()
    {
        _job.CommitMode = CommitMode.PullRequest;
        await SaveJobAsync();
        _scripts.Items = [Proc("dbo", "P1")];
        _gitHub.CreatePullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PullRequestInfo(7, "https://github.com/o/n/pull/7", null)));
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);

        // Next run: states are current, but a stale prior state forces a re-write that produces an
        // IDENTICAL tree (commit reports no changes). On the recut head branch "ahead of origin"
        // counts the whole history — the engine must not use it to push a zero-diff branch and fail
        // PR creation with GitHub's 422 forever.
        File.Delete(Directory.EnumerateFiles(_root, "*.sql", SearchOption.AllDirectories).First());
        _gitWorkspace.CommitAllAsync(
                Arg.Any<GitWorkspaceContext>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GitCommitResult.NoChanges());
        _gitWorkspace.HasUnpushedCommitsAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(true); // what a fresh-cut branch reports
        _gitWorkspace.ClearReceivedCalls();
        _gitHub.ClearReceivedCalls();

        var run = await RunAsync();

        Assert.Equal(RunStatus.NoChanges, run.Status);
        await _gitWorkspace.DidNotReceive().PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    private static RawScriptedObject Proc(string schema, string name) =>
        RawScriptedObject.Scripted(
            new ScriptedObjectIdentity(SqlObjectType.StoredProcedure, schema, name),
            $"CREATE PROCEDURE {schema}.{name} AS SELECT 1;");

    private static RawScriptedObject View(string schema, string name) =>
        RawScriptedObject.Scripted(
            new ScriptedObjectIdentity(SqlObjectType.View, schema, name),
            $"CREATE VIEW {schema}.{name} AS SELECT 1 AS One;");

    private static RawScriptedObject Schema(string name) =>
        RawScriptedObject.Scripted(
            new ScriptedObjectIdentity(SqlObjectType.Schema, string.Empty, name),
            $"CREATE SCHEMA [{name}];");

    /// <summary>Honors the schema filter server-side, exactly like the real providers do.</summary>
    private sealed class FilteringScriptProvider : IObjectScriptProvider
    {
        public IReadOnlyList<RawScriptedObject> Items { get; set; } = [];

        public ScriptingStrategy Strategy => ScriptingStrategy.Metadata;

        public async IAsyncEnumerable<RawScriptedObject> ScriptAsync(
            ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var filter = request.Selection.SchemaFilter;
            // Schema objects carry their name in Name (schema attribute empty) — the real provider
            // filters them by name, like everything else is filtered by schema.
            foreach (var item in Items.Where(i =>
                request.Types.Contains(i.Identity.Type)
                && (filter.Count == 0 || filter.Contains(
                    i.Identity.Type == SqlObjectType.Schema ? i.Identity.Name : i.Identity.Schema))))
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
