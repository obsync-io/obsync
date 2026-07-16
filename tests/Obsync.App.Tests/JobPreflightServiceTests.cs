using System.IO;
using NSubstitute;
using Obsync.App.Services;
using Obsync.Data.Repositories;
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

    private readonly SqlConnectionProfile _connection = new() { Name = "Prod", ServerName = "SVR" };
    private readonly GitRepositoryProfile _repository = new() { Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" };

    private JobPreflightService Build() => new(_probe, _gitHub, _credentials, _jobs, new SystemClock());

    private JobPreflightRequest GitRequest(CommitMode mode = CommitMode.DirectCommit, string branch = "main") =>
        new(_connection, _repository, branch, mode, ExportPath: null, "environments/SVR/db1", EditingJobId: null);

    private void SqlSucceeds() =>
        _probe.TestConnectionAsync(Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SqlServerInfo { ProductVersion = "16.0.4100", Edition = "Enterprise Edition" }));

    private void GitHubSucceeds(bool canWrite = true, params string[] branches)
    {
        _credentials.Retrieve(CredentialKeys.GitHubToken(_repository.Id)).Returns("tok");
        _credentials.Exists(Arg.Any<string>()).Returns(true);
        _gitHub.CheckRepositoryAccessAsync("tok", "o", "r", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: canWrite, Detail: null)));
        _gitHub.GetBranchesAsync("tok", "o", "r", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>([.. branches]));
    }

    private void NoOtherJobs() =>
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([]));

    private static DiagnosticResult Single(IReadOnlyList<DiagnosticResult> results, string name) =>
        Assert.Single(results, r => r.Name == name);

    [Fact]
    public async Task Run_HealthyGitJob_EveryCheckPasses()
    {
        SqlSucceeds();
        GitHubSucceeds(canWrite: true, "main", "develop");
        NoOtherJobs();

        var results = await Build().RunAsync(GitRequest());

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(DiagnosticStatus.Pass, r.Status));
        Assert.Equal(
            ["SQL connection", "Repository access", "Branch 'main'", "Credentials", "Folder collision"],
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
