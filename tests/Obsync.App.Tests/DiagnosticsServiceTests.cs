using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Obsync.App.Services;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
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
        var schedulerHealth = Substitute.For<ISchedulerHealthService>();
        schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SchedulerHealth(SchedulerHealthStatus.NotInstalled, "not installed"));
        return new DiagnosticsService(probe, gitHub, git, credentials, serverRepo, repoRepo,
            Substitute.For<IProxyProvider>(), Substitute.For<IAppSettingsRepository>(), schedulerHealth,
            Substitute.For<Obsync.Data.IDbConnectionFactory>(), SystemClock.Instance);
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

    [Fact]
    public async Task RunAsync_StampsEveryResultWithACheckedAtTimestamp()
    {
        var git = Substitute.For<IGitCommandRunner>();
        git.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, "git version 2.45.0", string.Empty));

        var service = Build(git, Substitute.For<ISqlServerProbe>(), Substitute.For<IGitHubService>(),
            Substitute.For<ICredentialStore>(), [], []);

        var results = await service.RunAsync();

        Assert.All(results, r => Assert.NotEqual(default, r.CheckedAt));
    }

    // --- Credential Manager round-trip probe --------------------------------------------------------

    /// <summary>An in-memory credential store so the round-trip is exercised without touching Windows.</summary>
    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = [];

        public IReadOnlyDictionary<string, string> Secrets => _secrets;

        public void Store(string key, string secret) => _secrets[key] = secret;

        public string? Retrieve(string key) => _secrets.GetValueOrDefault(key);

        public void Delete(string key) => _secrets.Remove(key);

        public bool Exists(string key) => _secrets.ContainsKey(key);
    }

    [Fact]
    public void ProbeCredentialStore_RoundTripSucceeds_AndDeletesTheSentinel()
    {
        var store = new FakeCredentialStore();

        var result = DiagnosticsService.ProbeCredentialStore(store, DateTimeOffset.UnixEpoch);

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.CheckedAt);
        Assert.Empty(store.Secrets); // the sentinel never lingers in the vault
    }

    [Fact]
    public void ProbeCredentialStore_FailsWithActionableText_WhenTheValueDoesNotReadBack()
    {
        string? sentinel = null;
        var store = Substitute.For<ICredentialStore>();
        store.When(s => s.Store(Arg.Any<string>(), Arg.Any<string>())).Do(call => sentinel = call.ArgAt<string>(1));
        store.Retrieve(Arg.Any<string>()).Returns("something-else");

        var result = DiagnosticsService.ProbeCredentialStore(store, DateTimeOffset.UnixEpoch);

        Assert.Equal(DiagnosticStatus.Fail, result.Status);
        Assert.Contains("Credential Manager", result.Detail);
        // The random sentinel value never appears in the user-facing detail.
        Assert.NotNull(sentinel);
        Assert.DoesNotContain(sentinel, result.Detail);
    }

    [Fact]
    public void ProbeCredentialStore_FailsWithActionableText_WhenTheStoreThrows()
    {
        var store = Substitute.For<ICredentialStore>();
        store.When(s => s.Store(Arg.Any<string>(), Arg.Any<string>())).Throw(new InvalidOperationException("vault locked"));

        var result = DiagnosticsService.ProbeCredentialStore(store, DateTimeOffset.UnixEpoch);

        Assert.Equal(DiagnosticStatus.Fail, result.Status);
        Assert.Contains("vault locked", result.Detail);
    }

    // --- Folder writability probes -------------------------------------------------------------------

    [Fact]
    public void ProbeFolderWritable_PassesOnAWritableFolder_AndLeavesNoProbeFileBehind()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obsync-diag-{Guid.NewGuid():N}");
        try
        {
            var result = DiagnosticsService.ProbeFolderWritable("Data folder", dir, DateTimeOffset.UnixEpoch);

            Assert.Equal(DiagnosticStatus.Pass, result.Status);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProbeFolderWritable_FailsWhenThePathCannotBeAFolder()
    {
        // A path "inside" an existing file can never be created as a directory.
        var file = Path.Combine(Path.GetTempPath(), $"obsync-diag-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, "x");
        try
        {
            var result = DiagnosticsService.ProbeFolderWritable(
                "Workspaces folder", Path.Combine(file, "sub"), DateTimeOffset.UnixEpoch);

            Assert.Equal(DiagnosticStatus.Fail, result.Status);
            Assert.Contains("Cannot write to", result.Detail);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // --- State database probe --------------------------------------------------------------------------

    [Fact]
    public async Task ProbeStateDatabase_PassesWithSizeAndQuickCheck_OnAHealthyDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"obsync-diag-db-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(dbPath);
        await using var provider = services.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
            var factory = provider.GetRequiredService<IDbConnectionFactory>();

            var result = await DiagnosticsService.ProbeStateDatabaseAsync(
                dbPath, factory, DateTimeOffset.UnixEpoch, CancellationToken.None);

            Assert.Equal(DiagnosticStatus.Pass, result.Status);
            Assert.Contains("quick integrity check passed", result.Detail);
            Assert.Contains("B", result.Detail); // the size is part of the detail
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ProbeStateDatabase_FailsWhenTheFileIsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"obsync-diag-missing-{Guid.NewGuid():N}.db");

        var result = await DiagnosticsService.ProbeStateDatabaseAsync(
            missing, Substitute.For<IDbConnectionFactory>(), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Fail, result.Status);
        Assert.Contains("not found", result.Detail);
    }
}
