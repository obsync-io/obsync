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
/// Two guards were added to the engine after the 0.9.0 audit — the case-twin rejection and the
/// recording of a scheduled occurrence dropped by lock contention — and both were originally landed
/// with their <em>logic</em> unit-tested but their <em>wiring</em> verified only by reading. That is
/// the same gap the audit criticises elsewhere: a green suite that never exercises the path. These
/// drive the real <see cref="SyncEngine"/> against a real state database, faking only SQL scripting
/// and git, so a future edit that detaches either guard from the run fails here.
/// </summary>
public sealed class EngineGateWiringTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obsync-gates-{Guid.NewGuid():N}");

    private ServiceProvider _provider = null!;
    private FakeScriptProvider _scripts = null!;
    private IGitWorkspace _git = null!;
    private SyncJob _job = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _scripts = new FakeScriptProvider();

        _git = Substitute.For<IGitWorkspace>();
        var gitWorkspace = _git;
        gitWorkspace.PrepareAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        gitWorkspace.HasUnpushedCommitsAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(false);
        gitWorkspace.CommitAllAsync(
                Arg.Any<GitWorkspaceContext>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(GitCommitResult.Committed("abc1234def5678"));
        gitWorkspace.PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddObsyncData(Path.Combine(_root, "state.db"));
        services.AddObsyncShared();
        services.AddSingleton<IObjectScriptProvider>(_scripts);
        services.AddSingleton(gitWorkspace);
        services.AddSingleton(Substitute.For<IGitHubService>());
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
            Name = "gate-wiring",
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A cron cadence has no next-run this layer can compute — Obsync.Shared has no cron engine — so
    /// the post-run summary fell back to the cached value. That value is normally the fire time that
    /// just elapsed, and this write replaces the whole summary, so it re-asserted a past time over
    /// the future one the scheduler had already written, leaving a run that had just succeeded
    /// looking overdue five minutes later.
    /// </summary>
    [Fact]
    public async Task AFinishedCronRun_DoesNotReAssertTheFireTimeThatJustElapsed()
    {
        _scripts.Items = [Proc("Foo")];
        var jobs = _provider.GetRequiredService<IJobRepository>();
        _job.Schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = "0 0 3 * * ?" };
        await jobs.UpsertAsync(_job);
        await jobs.UpdateNextRunAtAsync(_job.Id, DateTimeOffset.UtcNow.AddMinutes(-30));

        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);

        var after = (await jobs.GetAsync(_job.Id))!;
        Assert.Null(after.RunSummary.NextRunAt);
        Assert.False(after.IsScheduleOverdue(DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    /// <summary>
    /// The window gate advances the cached next-run so the UI stays accurate — but for a cron
    /// cadence this layer has no next-run to advance to, and writing that null WIPED the value the
    /// scheduler had already put there. Blank cell, and an overdue signal that cannot fire because
    /// it has nothing to compare against. The window here opens two hours from now, so the run is
    /// outside it whatever time the suite is run at.
    /// </summary>
    [Fact]
    public async Task AnOccurrenceSkippedByTheWindow_DoesNotWipeACronJobsNextRun()
    {
        _scripts.Items = [Proc("Foo")];
        var jobs = _provider.GetRequiredService<IJobRepository>();
        var opens = TimeOnly.FromDateTime(DateTime.Now.AddHours(2));
        _job.Schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Cron,
            CronExpression = "0 0 3 * * ?",
            MaintenanceWindowEnabled = true,
            WindowStart = opens,
            WindowEnd = opens.AddHours(1),
        };
        await jobs.UpsertAsync(_job);
        var scheduled = DateTimeOffset.UtcNow.AddHours(6);
        await jobs.UpdateNextRunAtAsync(_job.Id, scheduled);

        var run = await RunAsync(RunTrigger.Scheduled);

        Assert.Equal(RunStatus.NoChanges, run.Status); // skipped by the window, not executed
        var after = (await jobs.GetAsync(_job.Id))!;
        Assert.NotNull(after.RunSummary.NextRunAt);
        Assert.Equal(scheduled.ToUnixTimeSeconds(), after.RunSummary.NextRunAt!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// The other half: a next-run still ahead of the run is the scheduler's own answer for the
    /// following occurrence, and must survive rather than be discarded along with the stale ones.
    /// </summary>
    [Fact]
    public async Task AFinishedCronRun_KeepsANextRunThatIsStillAhead()
    {
        _scripts.Items = [Proc("Foo")];
        var jobs = _provider.GetRequiredService<IJobRepository>();
        _job.Schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = "0 0 3 * * ?" };
        await jobs.UpsertAsync(_job);
        var scheduled = DateTimeOffset.UtcNow.AddHours(6);
        await jobs.UpdateNextRunAtAsync(_job.Id, scheduled);

        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);

        var after = (await jobs.GetAsync(_job.Id))!;
        Assert.NotNull(after.RunSummary.NextRunAt);
        Assert.Equal(scheduled.ToUnixTimeSeconds(), after.RunSummary.NextRunAt!.Value.ToUnixTimeSeconds());
    }

    private Task<SyncRun> RunAsync(RunTrigger trigger = RunTrigger.Manual) =>
        _provider.GetRequiredService<ISyncEngine>().RunJobAsync(_job.Id, trigger);

    /// <summary>
    /// Takes the job's cross-process lock, standing in for a run already in flight elsewhere.
    /// Polls rather than trying once: the engine's own lock file is DeleteOnClose, and Windows
    /// keeps the directory entry until the last handle closes, so an acquire immediately after a
    /// run can transiently fail. The product polls for the same reason (JobRunLock.WaitAsync).
    /// </summary>
    private static IDisposable AcquireJobLock(Guid jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (JobRunLock.TryAcquire(ObsyncPaths.LocksRoot, jobId) is { } handle)
            {
                return handle;
            }

            Thread.Sleep(20);
        }

        throw new InvalidOperationException("The test could not take the job's run lock.");
    }

    private static RawScriptedObject Proc(string name) =>
        RawScriptedObject.Scripted(
            new ScriptedObjectIdentity(SqlObjectType.StoredProcedure, "dbo", name),
            $"CREATE PROCEDURE dbo.{name} AS BEGIN SELECT 1; END");

    // --- Case twins (the H-2 guard) ---------------------------------------------------------

    /// <summary>
    /// Two objects whose names differ only by case reach the engine from a case-sensitive-collation
    /// server. They map to two paths that are one file on Windows, so the run must stop rather than
    /// let one silently overwrite the other.
    /// </summary>
    [Fact]
    public async Task ARunCarryingCaseTwins_FailsAndNamesBothObjects()
    {
        _scripts.Items = [Proc("Foo"), Proc("FOO")];

        var run = await RunAsync();

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.NotNull(run.ErrorMessage);
        Assert.Contains("differ only by letter case", run.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("dbo.Foo", run.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("dbo.FOO", run.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The delivered gate must still hold: a run stopped by the guard may not advance object state,
    /// or the next run would report "no changes" for objects that were never written.
    /// </summary>
    [Fact]
    public async Task ARunCarryingCaseTwins_AdvancesNoObjectState()
    {
        _scripts.Items = [Proc("Foo"), Proc("FOO")];

        await RunAsync();

        var states = await _provider.GetRequiredService<IObjectStateRepository>()
            .GetForJobDatabaseAsync(_job.Id, "Db1");
        Assert.Empty(states);
    }

    /// <summary>The guard must not fire on ordinary objects — that would break every real run.</summary>
    [Fact]
    public async Task ARunWithoutCaseTwins_SucceedsNormally()
    {
        _scripts.Items = [Proc("Foo"), Proc("Bar"), Proc("Baz")];

        var run = await RunAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.ObjectsAdded);
    }

    // --- Dropped scheduled occurrence (the H-5 gate) ------------------------------------------

    /// <summary>
    /// A scheduled occurrence that cannot start because a previous run of the same job holds the
    /// cross-process lock used to vanish entirely. It must now leave a row.
    /// </summary>
    [Fact]
    public async Task AScheduledOccurrenceBlockedByTheJobLock_IsRecorded()
    {
        _scripts.Items = [Proc("Foo")];

        using var held = AcquireJobLock(_job.Id);

        var run = await RunAsync(RunTrigger.Scheduled);

        Assert.Equal(RunStatus.Skipped, run.Status);
        Assert.NotEmpty(run.RunKey); // an empty key renders as a blank History cell
        Assert.NotNull(run.ErrorMessage);

        var persisted = await _provider.GetRequiredService<IRunRepository>().GetForJobAsync(_job.Id, 10);
        var row = Assert.Single(persisted);
        Assert.Equal(RunStatus.Skipped, row.Status);
        Assert.Equal(run.Id, row.Id);
        Assert.Contains("still active", row.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A skip is not a run outcome: overwriting the summary would hide the last real sync's result
    /// from every dashboard tile, and is what the dashboard's skip row exists to avoid.
    /// </summary>
    [Fact]
    public async Task ADroppedOccurrence_LeavesTheJobsRunSummaryAlone()
    {
        _scripts.Items = [Proc("Foo")];
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);

        var before = (await _provider.GetRequiredService<IJobRepository>().GetAsync(_job.Id))!.RunSummary;

        using (AcquireJobLock(_job.Id))
        {
            Assert.Equal(RunStatus.Skipped, (await RunAsync(RunTrigger.Scheduled)).Status);
        }

        var after = (await _provider.GetRequiredService<IJobRepository>().GetAsync(_job.Id))!.RunSummary;
        Assert.Equal(RunStatus.Succeeded, after.LastStatus);
        Assert.Equal(before.LastRunId, after.LastRunId);
        Assert.Equal(before.LastRunAt, after.LastRunAt);
    }

    /// <summary>
    /// A manual run keeps throwing instead: the user is present, so an error they can read beats a
    /// history row they have to go looking for.
    /// </summary>
    [Fact]
    public async Task AManualRunBlockedByTheJobLock_ThrowsAndRecordsNothing()
    {
        _scripts.Items = [Proc("Foo")];

        using var held = AcquireJobLock(_job.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(RunTrigger.Manual));

        Assert.Empty(await _provider.GetRequiredService<IRunRepository>().GetForJobAsync(_job.Id, 10));
    }

    // --- Direct-mode push failure and stranded-commit recovery -------------------------------

    private void SetPush(bool ok) =>
        _git.PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>())
            .Returns(ok
                ? Result.Success()
                : Result.Failure("remote: error: GH006: Protected branch update failed for refs/heads/main."));

    private void SetUnpushed(bool has) =>
        _git.HasUnpushedCommitsAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>()).Returns(has);

    private void SetCommitProduced(bool produced) =>
        _git.CommitAllAsync(
                Arg.Any<GitWorkspaceContext>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(produced ? GitCommitResult.Committed("abc1234def5678") : GitCommitResult.NoChanges());

    private async Task DirectModeAsync()
    {
        _job.CommitMode = CommitMode.DirectCommit;
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
    }

    /// <summary>
    /// A commit that never reached GitHub must not be advertised as a GitHub link. The URL is
    /// assigned right after the commit, long before the push is attempted, and it is persisted to
    /// the run row that the History view, the exported report, the alert email and the webhook
    /// payload all read.
    /// </summary>
    [Fact]
    public async Task AFailedDirectPush_LeavesNoGitHubCommitUrl_ButKeepsTheSha()
    {
        await DirectModeAsync();
        _scripts.Items = [Proc("Foo")];
        SetCommitProduced(true);
        SetPush(ok: false);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Warning, run.Status);
        Assert.Null(run.CommitUrl);
        Assert.Equal("abc1234def5678", run.CommitSha); // the commit is real, just not delivered
        Assert.Contains("push", run.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var persisted = Assert.Single(await _provider.GetRequiredService<IRunRepository>().GetForJobAsync(_job.Id, 10));
        Assert.Null(persisted.CommitUrl);
    }

    /// <summary>A push that works still gets its link — the fix must not remove the ordinary case.</summary>
    [Fact]
    public async Task ASuccessfulDirectPush_StillRecordsTheCommitUrl()
    {
        await DirectModeAsync();
        _scripts.Items = [Proc("Foo")];
        SetCommitProduced(true);
        SetPush(ok: true);

        var run = await RunAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("https://github.com/o/n/commit/abc1234def5678", run.CommitUrl);
    }

    /// <summary>
    /// The recovery the engine's own comment promises — "the stranded commit is preserved and
    /// re-pushed by the next run" — had no engine-level coverage: every fixture stubbed
    /// HasUnpushedCommitsAsync to false, so a refactor could detach it and the suite would stay
    /// green. This drives the no-new-changes run and asserts the push is retried.
    /// </summary>
    [Fact]
    public async Task ALaterRunWithNoChanges_RepushesAStrandedCommit()
    {
        await DirectModeAsync();
        _scripts.Items = [Proc("Foo")];
        SetCommitProduced(true);
        SetPush(ok: false);
        SetUnpushed(true);
        Assert.Equal(RunStatus.Warning, (await RunAsync()).Status);

        // Second run: the object is unchanged, so nothing new is committed — but the commit from
        // the first run is still sitting in the local clone.
        _git.ClearReceivedCalls();
        SetCommitProduced(false);
        SetPush(ok: true);

        var recovered = await RunAsync();

        Assert.Equal(RunStatus.Succeeded, recovered.Status);
        await _git.Received(1).PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The mirror image: with nothing stranded, a no-change run must not push. Without this the
    /// test above would pass against an engine that pushed unconditionally.
    /// </summary>
    [Fact]
    public async Task ALaterRunWithNoChangesAndNothingStranded_DoesNotPush()
    {
        await DirectModeAsync();
        _scripts.Items = [Proc("Foo")];
        SetCommitProduced(true);
        SetPush(ok: true);
        SetUnpushed(false);
        Assert.Equal(RunStatus.Succeeded, (await RunAsync()).Status);

        _git.ClearReceivedCalls();
        SetCommitProduced(false);

        var second = await RunAsync();

        Assert.Equal(RunStatus.NoChanges, second.Status);
        await _git.DidNotReceive().PushAsync(Arg.Any<GitWorkspaceContext>(), Arg.Any<CancellationToken>());
    }

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
