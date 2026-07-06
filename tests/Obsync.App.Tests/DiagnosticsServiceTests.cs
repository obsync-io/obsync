using System.Linq;
using NSubstitute;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Git;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Covers the diagnostics aggregation: git version -> Pass, a failing SQL probe -> Fail, a read-only
/// GitHub token -> Warning, and a repo with no stored token -> Warning.
/// </summary>
public sealed class DiagnosticsServiceTests
{
    private static DiagnosticsService Build(
        IGitCommandRunner git, ISqlServerProbe probe, IGitHubService gitHub, ICredentialStore credentials,
        IReadOnlyList<SqlConnectionProfile> servers, IReadOnlyList<GitRepositoryProfile> repositories)
    {
        var serverRepo = Substitute.For<IConnectionProfileRepository>();
        serverRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(servers));
        var repoRepo = Substitute.For<IRepositoryProfileRepository>();
        repoRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(repositories));
        return new DiagnosticsService(probe, gitHub, git, credentials, serverRepo, repoRepo,
            Substitute.For<IProxyProvider>(), Substitute.For<IAppSettingsRepository>());
    }

    [Fact]
    public async Task RunAsync_ReportsGitPass_SqlFail_ReadOnlyRepoWarning()
    {
        var git = Substitute.For<IGitCommandRunner>();
        git.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, "git version 2.45.0", string.Empty));

        var probe = Substitute.For<ISqlServerProbe>();
        probe.TestConnectionAsync(Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SqlServerInfo>("Login failed for user 'svc'."));

        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: false, Detail: null)));

        var credentials = Substitute.For<ICredentialStore>();
        credentials.Retrieve(Arg.Any<string>()).Returns("tok");

        var service = Build(git, probe, gitHub, credentials,
            [new SqlConnectionProfile { Name = "PROD", ServerName = "s" }],
            [new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" }]);

        var results = await service.RunAsync();

        Assert.Contains(results, r => r.Name == "Git CLI" && r.Status == DiagnosticStatus.Pass);
        Assert.Contains(results, r => r.Name.StartsWith("SQL") && r.Status == DiagnosticStatus.Fail);
        Assert.Contains(results, r => r.Name.StartsWith("GitHub") && r.Status == DiagnosticStatus.Warning);
    }

    [Fact]
    public async Task RunAsync_RepoWithoutToken_IsWarning_AndGitHubIsNotCalled()
    {
        var git = Substitute.For<IGitCommandRunner>();
        git.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, "git version 2.45.0", string.Empty));

        var gitHub = Substitute.For<IGitHubService>();
        var credentials = Substitute.For<ICredentialStore>();
        credentials.Retrieve(Arg.Any<string>()).Returns((string?)null); // no stored token

        var service = Build(git, Substitute.For<ISqlServerProbe>(), gitHub, credentials,
            [], [new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" }]);

        var results = await service.RunAsync();

        var repoResult = results.Single(r => r.Name.StartsWith("GitHub"));
        Assert.Equal(DiagnosticStatus.Warning, repoResult.Status);
        await gitHub.DidNotReceive().CheckRepositoryAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
