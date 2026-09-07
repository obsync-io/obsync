using System.IO;
using NSubstitute;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Git;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The Review-step preflight aggregation: per-check pass/fail mapping, mode-aware verdicts
/// (read-only tokens, missing branches), Export Only skipping GitHub entirely, folder-collision
/// detection, and one failing check never stopping the rest.
/// </summary>
public sealed class JobPreflightServiceTests
{
    private readonly ISqlServerProbe _probe = Substitute.For<ISqlServerProbe>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IGitRemoteProbe _gitRemote = Substitute.For<IGitRemoteProbe>();
    private readonly IProxyProvider _proxy = Substitute.For<IProxyProvider>();
    private readonly IAppSettingsRepository _settings = Substitute.For<IAppSettingsRepository>();
    private readonly ISchedulerHealthService _schedulerHealth = Substitute.For<ISchedulerHealthService>();

    private readonly SqlConnectionProfile _connection = new() { Name = "Prod", ServerName = "SVR" };
    private readonly GitRepositoryProfile _repository = new() { Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" };

    private JobPreflightService Build() => new(_probe, _gitHub, _credentials, _jobs, new SystemClock(), _gitRemote, _proxy, _settings, _schedulerHealth);

    private JobPreflightRequest GitRequest(CommitMode mode = CommitMode.DirectCommit, string branch = "main") =>
        new(_connection, _repository, branch, mode, ExportPath: null, "environments/SVR/db1", EditingJobId: null);

    private void SqlSucceeds() =>
        _probe.TestConnectionAsync(Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SqlServerInfo { ProductVersion = "16.0.4100", Edition = "Enterprise Edition" }));

    private void GitHubSucceeds(bool canWrite = true, params string[] branches)
    {
        GitTransportSucceeds();
        _credentials.Retrieve(CredentialKeys.GitHubToken(_repository.Id)).Returns("tok");
        _credentials.Exists(Arg.Any<string>()).Returns(true);
        _gitHub.CheckRepositoryAccessAsync("tok", "o", "r", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: canWrite, Detail: null)));
        _gitHub.GetBranchesAsync("tok", "o", "r", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>([.. branches]));
    }

    /// <summary>Service runs as this user — the case where the other checks DO transfer.</summary>
    private void SameAccountService() =>
        _schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SchedulerHealth(SchedulerHealthStatus.Healthy, "healthy", CurrentActor.Name));

    /// <summary>Makes the git transport probe succeed — the default substitute returns a null Result.</summary>
    private void GitTransportSucceeds()
    {
        SameAccountService();
        _gitRemote.CheckAsync(Arg.Any<GitNetworkOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _proxy.ResolveAsync(Arg.Any<CancellationToken>()).Returns((ProxyResolution?)null);
        _settings.GetGitTlsAsync(Arg.Any<CancellationToken>()).Returns(new GitTlsSettings());

        // Unprotected by default. Protection is warned about, not failed, so a substitute returning
        // "protected" would turn every healthy-path assertion into a warning.
        _gitHub.IsBranchProtectedAsync("tok", "o", "r", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(false));

        // No ruleset by default, for the same reason. This must be stubbed explicitly: NSubstitute
        // returns null for an unconfigured method whose return type is a concrete class, and a null
        // Result is not a shape any real implementation can produce.
        _gitHub.GetBranchRulesAsync("tok", "o", "r", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>([]));
    }

    private void NoOtherJobs() =>
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([]));

    private static DiagnosticResult Single(IReadOnlyList<DiagnosticResult> results, string name) =>
        Assert.Single(results, r => r.Name == name);

    private JobPreflightRequest RequestWithDatabases(params string[] databases) =>
        new(_connection, _repository, "main", CommitMode.DirectCommit, ExportPath: null,
            "environments/SVR/db1", EditingJobId: null, databases);

    private void PermissionsReturn(string database, SqlDatabasePermissionReport report) =>
        _probe.CheckDatabasePermissionsAsync(Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), database, Arg.Any<CancellationToken>())
            .Returns(Result.Success(report));

    private static ScheduleProfile Daily() => new() { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(23, 0) };

    private JobPreflightRequest ScheduledRequest() =>
        new(_connection, _repository, "main", CommitMode.DirectCommit, ExportPath: null,
            "environments/SVR/db1", EditingJobId: null, Databases: null, Schedule: Daily());

    [Fact]
    public async Task AScheduledJob_WarnsWhenTheServiceRunsAsADifferentAccount()
    {
        // The check that makes the others honest. Everything above it ran in THIS process as the
        // signed-in user; a scheduled run executes in the service under its own account, and both the
        // credential vault and %LOCALAPPDATA% are per-account. Without this row a job could pass every
        // check and then fail every scheduled run on a missing token.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SchedulerHealth(SchedulerHealthStatus.Healthy, "healthy", @"NT AUTHORITY\SYSTEM"));

        var identity = Single(await Build().RunAsync(ScheduledRequest()), "Run identity");

        Assert.Equal(DiagnosticStatus.Warning, identity.Status);
        Assert.Contains(@"NT AUTHORITY\SYSTEM", identity.Detail);
        Assert.Contains("PER ACCOUNT", identity.Detail);
        Assert.Contains("obsync credential set", identity.Detail);
    }

    [Fact]
    public async Task AScheduledJob_PassesWhenTheServiceRunsAsTheSameAccount()
    {
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();

        var identity = Single(await Build().RunAsync(ScheduledRequest()), "Run identity");

        Assert.Equal(DiagnosticStatus.Pass, identity.Status);
        Assert.Contains(CurrentActor.Name, identity.Detail);
    }

    [Fact]
    public async Task AManualOnlyJob_HasNoSecondIdentityToReconcile()
    {
        // Manual runs execute in this process, so the checks above genuinely do transfer. Warning
        // here would be noise on the common case.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SchedulerHealth(SchedulerHealthStatus.Healthy, "healthy", @"NT AUTHORITY\SYSTEM"));

        var identity = Single(await Build().RunAsync(GitRequest()), "Run identity");

        Assert.Equal(DiagnosticStatus.Pass, identity.Status);
        Assert.Contains("Manual runs only", identity.Detail);
    }

    [Fact]
    public async Task AnUnknownServiceAccount_WarnsRatherThanClaimingItIsFine()
    {
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SchedulerHealth(SchedulerHealthStatus.NotInstalled, "not installed", null));

        var identity = Single(await Build().RunAsync(ScheduledRequest()), "Run identity");

        Assert.Equal(DiagnosticStatus.Warning, identity.Status);
    }

    [Fact]
    public async Task AFailingGitTransport_FailsPreflight_WhileTheApiChecksStillPass()
    {
        // This is the incident that started the review: the REST checks go to api.github.com over
        // HttpClient (which does not check certificate revocation), the run goes to github.com over
        // MinGit and schannel (which does). So the dialog showed five green ticks and the first run
        // died on git clone. The two rows disagreeing IS the diagnosis, so both must be present.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitRemote.CheckAsync(Arg.Any<GitNetworkOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("The network blocked git's certificate revocation check."));

        var results = await Build().RunAsync(GitRequest());

        var transport = Single(results, "Git connection");
        Assert.Equal(DiagnosticStatus.Fail, transport.Status);
        Assert.Contains("revocation", transport.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DiagnosticStatus.Pass, Single(results, "Repository access").Status);
    }

    [Fact]
    public async Task AProtectedBranch_WarnsInDirectCommitMode()
    {
        // permissions.push is the collaborator ROLE and stays true under branch protection, so
        // "Write / push — Contents ✓" was vouching for something it never checked. The engine already
        // ships a GH006 explanation for the rejection this produces.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitHub.IsBranchProtectedAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Success(true));

        var branch = Single(await Build().RunAsync(GitRequest()), "Branch 'main'");

        Assert.Equal(DiagnosticStatus.Warning, branch.Status);
        Assert.Contains("PROTECTED", branch.Detail);
        Assert.Contains("signed commits", branch.Detail);
    }

    [Fact]
    public async Task ARuleset_IsWarnedAbout_EvenWhenTheClassicProtectionFlagIsFalse()
    {
        // The gap that let a job pass preflight and then die on the push. Rulesets are GitHub's
        // current mechanism and reject with GH013; classic branch protection is the legacy one and
        // rejects with GH006. This check only ever read the branch object's `protected` flag, which
        // was built for the legacy system.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitHub.IsBranchProtectedAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Success(false));
        _gitHub.GetBranchRulesAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>(["pull_request"]));

        var branch = Single(await Build().RunAsync(GitRequest()), "Branch 'main'");

        Assert.Equal(DiagnosticStatus.Warning, branch.Status);
        Assert.Contains("GH013", branch.Detail);
        // Naming the rule is the point — a boolean could never have done this.
        Assert.Contains("pull request", branch.Detail);
    }

    [Fact]
    public async Task AFailedRulesetLookup_FallsBackToTheClassicCheck()
    {
        // One endpoint being unavailable must not silently downgrade the check to nothing.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitHub.GetBranchRulesAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<string>>("GitHub error: 404"));
        _gitHub.IsBranchProtectedAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Success(true));

        var branch = Single(await Build().RunAsync(GitRequest()), "Branch 'main'");

        Assert.Equal(DiagnosticStatus.Warning, branch.Status);
        Assert.Contains("PROTECTED", branch.Detail);
    }

    [Fact]
    public async Task ARuleset_IsIgnoredInPullRequestMode()
    {
        // PR mode pushes to its own head branch and merges through review, which is exactly what a
        // pull-request rule is asking for.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitHub.GetBranchRulesAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>(["pull_request"]));

        var branch = Single(await Build().RunAsync(GitRequest(CommitMode.PullRequest)), "Branch 'main'");

        Assert.Equal(DiagnosticStatus.Pass, branch.Status);
    }

    [Fact]
    public async Task AProtectedBaseBranch_IsFineInPullRequestMode()
    {
        // PR mode pushes to its own head branch and merges through review — which is what protection
        // is asking for. Warning here would be noise on a correctly configured job.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitHub.IsBranchProtectedAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Success(true));

        var branch = Single(await Build().RunAsync(GitRequest(CommitMode.PullRequest)), "Branch 'main'");

        Assert.Equal(DiagnosticStatus.Pass, branch.Status);
    }

    [Fact]
    public async Task AFailedProtectionLookup_DoesNotInventAWarning()
    {
        // An admin-only endpoint or a network blip is not evidence of protection.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _gitHub.IsBranchProtectedAsync("tok", "o", "r", "main", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<bool>("GitHub error: 403"));

        Assert.Equal(DiagnosticStatus.Pass, Single(await Build().RunAsync(GitRequest()), "Branch 'main'").Status);
    }

    [Fact]
    public async Task UnreadableDefinitions_FailPreflight()
    {
        // The defect this closes: without VIEW DEFINITION, SQL Server returns NULL from
        // sys.sql_modules.definition rather than erroring, SMO scripts the objects as empty, and the
        // run reports Succeeded while committing a hollowed-out schema over a correct one. A
        // connection test cannot see this — it opens the login's DEFAULT database and reads three
        // SERVERPROPERTY values, touching no per-database permission at all.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        PermissionsReturn("Sales", new SqlDatabasePermissionReport(
            "Sales", HasViewDefinition: false, HasViewDatabaseState: true,
            VisibleObjects: 420, Modules: 180, UnreadableModules: 180));

        var results = await Build().RunAsync(RequestWithDatabases("Sales"));

        var permissions = Single(results, "SQL permissions");
        Assert.Equal(DiagnosticStatus.Fail, permissions.Status);
        Assert.Contains("EMPTY", permissions.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VIEW DEFINITION", permissions.Detail);

        // The old connection check still passes — which is exactly why it was never enough.
        Assert.Equal(DiagnosticStatus.Pass, Single(results, "SQL connection").Status);
    }

    [Fact]
    public async Task ADatabaseWithNoVisibleObjects_WarnsRatherThanFails()
    {
        // Genuinely empty is possible, and only the run itself can tell that apart from invisible —
        // which is what the mass-deletion safety stop is for. A hard failure here would block a
        // legitimate new database.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        PermissionsReturn("Fresh", new SqlDatabasePermissionReport(
            "Fresh", HasViewDefinition: true, HasViewDatabaseState: true,
            VisibleObjects: 0, Modules: 0, UnreadableModules: 0));

        var results = await Build().RunAsync(RequestWithDatabases("Fresh"));

        Assert.Equal(DiagnosticStatus.Warning, Single(results, "SQL permissions").Status);
    }

    [Fact]
    public async Task ADatabaseTheLoginCannotOpen_FailsAndNamesIt()
    {
        // CONNECT is granted per database, so a login with no user in this one fails here with
        // 916/4060 — a failure a default-database connection test never reaches.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        _probe.CheckDatabasePermissionsAsync(
                Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), "Locked", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SqlDatabasePermissionReport>("The server principal is not able to access the database."));

        var results = await Build().RunAsync(RequestWithDatabases("Locked"));

        var permissions = Single(results, "SQL permissions");
        Assert.Equal(DiagnosticStatus.Fail, permissions.Status);
        Assert.Contains("Locked", permissions.Detail);
    }

    [Fact]
    public async Task ReadableDefinitions_Pass()
    {
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();
        PermissionsReturn("Sales", new SqlDatabasePermissionReport(
            "Sales", HasViewDefinition: true, HasViewDatabaseState: true,
            VisibleObjects: 420, Modules: 180, UnreadableModules: 0));

        var results = await Build().RunAsync(RequestWithDatabases("Sales"));

        Assert.Equal(DiagnosticStatus.Pass, Single(results, "SQL permissions").Status);
    }

    [Fact]
    public async Task WithNoDatabasesSelected_ThePermissionCheckIsOmittedEntirely()
    {
        // Rather than reporting a green row it did not earn — the failure mode this whole check
        // exists to remove.
        SqlSucceeds();
        GitHubSucceeds(branches: "main");
        NoOtherJobs();

        var results = await Build().RunAsync(GitRequest());

        Assert.DoesNotContain(results, r => r.Name == "SQL permissions");
    }

    [Fact]
    public async Task Run_HealthyGitJob_EveryCheckPasses()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: true, "main", "develop");
        NoOtherJobs();

        var results = await Build().RunAsync(GitRequest());

        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.Equal(DiagnosticStatus.Pass, r.Status));

        // "Git connection" sits immediately after the API checks and before the local ones. It is a
        // separate row on purpose: it is the only one that proves the transport a RUN uses, and when
        // it disagrees with "Repository access" the disagreement is itself the diagnosis — API pass
        // with git fail means TLS or proxy, the reverse means token scope or SSO.
        // "Run identity" sits last among the local checks because it qualifies all the ones before
        // it: they ran in this process, as this user, and it says whether that is the identity a
        // scheduled run will use.
        Assert.Equal(
            ["SQL connection", "Repository access", "Branch 'main'", "Git connection", "Credentials", "Run identity", "Folder collision"],
            results.Select(r => r.Name));
    }

    [Fact]
    public async Task Run_ExportOnly_SkipsGitHubEntirely_AndProbesTheExportFolder()
    {
        SqlSucceeds();
        _credentials.Exists(Arg.Any<string>()).Returns(true);
        var exportDir = Path.Combine(Path.GetTempPath(), $"obsync-preflight-{Guid.NewGuid():N}");
        var request = new JobPreflightRequest(
            _connection, Repository: null, Branch: string.Empty, CommitMode.ExportOnly,
            exportDir, "environments/SVR", EditingJobId: null);

        try
        {
            var results = await Build().RunAsync(request);

            Assert.Equal(DiagnosticStatus.Pass, Single(results, "Export destination").Status);
            Assert.DoesNotContain(results, r => r.Name.StartsWith("Repository") || r.Name.StartsWith("Branch") || r.Name == "Folder collision");
            await _gitHub.DidNotReceiveWithAnyArgs().CheckRepositoryAccessAsync(default!, default!, default!);
            await _gitHub.DidNotReceiveWithAnyArgs().GetBranchesAsync(default!, default!, default!);
        }
        finally
        {
            Directory.Delete(exportDir, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ExportDestinationBlockedByAFile_Fails()
    {
        SqlSucceeds();
        _credentials.Exists(Arg.Any<string>()).Returns(true);
        var blockingFile = Path.Combine(Path.GetTempPath(), $"obsync-preflight-{Guid.NewGuid():N}.txt");
        File.WriteAllText(blockingFile, "in the way");
        var request = new JobPreflightRequest(
            _connection, Repository: null, Branch: string.Empty, CommitMode.ExportOnly,
            blockingFile, "environments/SVR", EditingJobId: null);

        try
        {
            var results = await Build().RunAsync(request);
            Assert.Equal(DiagnosticStatus.Fail, Single(results, "Export destination").Status);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task Run_NoStoredToken_RepositorySkippedWithWarning_AndCredentialsFail()
    {
        SqlSucceeds();
        NoOtherJobs();
        _credentials.Retrieve(Arg.Any<string>()).Returns((string?)null);
        _credentials.Exists(Arg.Any<string>()).Returns(false);

        var results = await Build().RunAsync(GitRequest());

        Assert.Equal(DiagnosticStatus.Warning, Single(results, "Repository access").Status);
        var credentials = Single(results, "Credentials");
        Assert.Equal(DiagnosticStatus.Fail, credentials.Status);
        Assert.Contains("GitHub access token", credentials.Detail);
        await _gitHub.DidNotReceiveWithAnyArgs().CheckRepositoryAccessAsync(default!, default!, default!);
    }

    [Fact]
    public async Task Run_ReadOnlyToken_WarnsForDirectCommit_ButPassesForLocalCommitOnly()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: false, "main");
        NoOtherJobs();

        var direct = await Build().RunAsync(GitRequest(CommitMode.DirectCommit));
        Assert.Equal(DiagnosticStatus.Warning, Single(direct, "Repository access").Status);

        var local = await Build().RunAsync(GitRequest(CommitMode.LocalCommitOnly));
        Assert.Equal(DiagnosticStatus.Pass, Single(local, "Repository access").Status);
    }

    [Fact]
    public async Task Run_MissingBranch_WarnsForDirectCommit_ButFailsForPullRequestBase()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: true, "develop"); // "main" does not exist
        NoOtherJobs();

        var direct = await Build().RunAsync(GitRequest(CommitMode.DirectCommit));
        Assert.Equal(DiagnosticStatus.Warning, Single(direct, "Branch 'main'").Status);

        var pullRequest = await Build().RunAsync(GitRequest(CommitMode.PullRequest));
        Assert.Equal(DiagnosticStatus.Fail, Single(pullRequest, "Branch 'main'").Status);
    }

    [Fact]
    public async Task Run_SqlProbeThrows_FailsThatCheck_ButTheRestStillRun()
    {
        _probe.TestConnectionAsync(Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<SqlServerInfo>>>(_ => throw new InvalidOperationException("network down"));
        GitHubSucceeds(canWrite: true, "main");
        NoOtherJobs();

        var results = await Build().RunAsync(GitRequest());

        var sql = Single(results, "SQL connection");
        Assert.Equal(DiagnosticStatus.Fail, sql.Status);
        Assert.Contains("network down", sql.Detail);
        Assert.Equal(DiagnosticStatus.Pass, Single(results, "Repository access").Status);
        Assert.Equal(DiagnosticStatus.Pass, Single(results, "Folder collision").Status);
    }

    [Fact]
    public async Task Run_AnotherJobOnTheSameRepoFolder_WarnsCaseInsensitively()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: true, "main");
        var other = new SyncJob
        {
            Name = "Nightly estate",
            RepositoryProfileId = _repository.Id,
            DestinationFolder = "ENVIRONMENTS/svr/DB1", // differs only in case
        };
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([other]));

        var results = await Build().RunAsync(GitRequest());

        var collision = Single(results, "Folder collision");
        Assert.Equal(DiagnosticStatus.Warning, collision.Status);
        Assert.Contains("Nightly estate", collision.Detail);
    }

    [Fact]
    public async Task Run_TheJobBeingEdited_IsNotItsOwnCollision()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: true, "main");
        var editing = new SyncJob
        {
            Name = "Myself",
            RepositoryProfileId = _repository.Id,
            DestinationFolder = "environments/SVR/db1",
        };
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([editing]));

        var results = await Build().RunAsync(GitRequest() with { EditingJobId = editing.Id });

        Assert.Equal(DiagnosticStatus.Pass, Single(results, "Folder collision").Status);
    }

    [Fact]
    public async Task Run_WindowsAuthConnection_NeedsNoSqlPasswordCredential()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: true, "main");
        NoOtherJobs();
        _credentials.Exists(CredentialKeys.SqlPassword(_connection.Id)).Returns(false); // none stored — and none needed

        var results = await Build().RunAsync(GitRequest());

        Assert.Equal(DiagnosticStatus.Pass, Single(results, "Credentials").Status);
    }
}
