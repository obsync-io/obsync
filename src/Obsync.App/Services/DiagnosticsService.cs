using System.IO;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Git;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>The health of a single diagnostic check.</summary>
public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail,
}

/// <summary>The result of one diagnostic check, shown as a pass/warn/fail row.</summary>
public sealed record DiagnosticResult(string Name, DiagnosticStatus Status, string Detail, DateTimeOffset CheckedAt);

/// <summary>Runs the environment health checks surfaced on the Settings → Diagnostics card.</summary>
public interface IDiagnosticsService
{
    Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken = default);

    /// <summary>The resolved git version + source (the same probe the Git CLI check runs).</summary>
    Task<string> GetGitVersionAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiagnosticsService" />
public sealed class DiagnosticsService : IDiagnosticsService
{
    private const long LowDiskThresholdBytes = 1L * 1024 * 1024 * 1024; // 1 GB

    /// <summary>The round-trip sentinel's key; the stored value is a random GUID that is never logged.</summary>
    internal const string CredentialProbeKey = "Obsync:diagnostic-probe";

    private readonly ISqlServerProbe _probe;
    private readonly IGitHubService _gitHub;
    private readonly IGitCommandRunner _git;
    private readonly IGitRemoteProbe _gitRemote;
    private readonly ICredentialStore _credentials;
    private readonly IConnectionProfileRepository _servers;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IProxyProvider _proxy;
    private readonly IAppSettingsRepository _settings;
    private readonly ISchedulerHealthService _schedulerHealth;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public DiagnosticsService(
        ISqlServerProbe probe,
        IGitHubService gitHub,
        IGitCommandRunner git,
        IGitRemoteProbe gitRemote,
        ICredentialStore credentials,
        IConnectionProfileRepository servers,
        IRepositoryProfileRepository repositories,
        IProxyProvider proxy,
        IAppSettingsRepository settings,
        ISchedulerHealthService schedulerHealth,
        IDbConnectionFactory connectionFactory,
        IClock clock)
    {
        _probe = probe;
        _gitHub = gitHub;
        _git = git;
        _gitRemote = gitRemote;
        _credentials = credentials;
        _servers = servers;
        _repositories = repositories;
        _proxy = proxy;
        _settings = settings;
        _schedulerHealth = schedulerHealth;
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var workspacesRoot = await GetEffectiveWorkspacesRootAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<DiagnosticResult>
        {
            await CheckGitAsync(cancellationToken).ConfigureAwait(false),
            ProbeCredentialStore(_credentials, _clock.UtcNow),
            ProbeFolderWritable("Data folder", ObsyncPaths.Root, _clock.UtcNow),
            ProbeFolderWritable("Workspaces folder", workspacesRoot, _clock.UtcNow),
            await ProbeStateDatabaseAsync(ObsyncPaths.DatabasePath, _connectionFactory, _clock.UtcNow, cancellationToken).ConfigureAwait(false),
            CheckDiskSpace(workspacesRoot),
            await CheckSchedulerAsync(cancellationToken).ConfigureAwait(false),
            await CheckProxyAsync(cancellationToken).ConfigureAwait(false),
        };

        foreach (var server in await _servers.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(await CheckServerAsync(server, cancellationToken).ConfigureAwait(false));
        }

        foreach (var repository in await _repositories.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(await CheckRepositoryAsync(repository, cancellationToken).ConfigureAwait(false));
            results.Add(await CheckRepositoryTransportAsync(repository, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<string> GetGitVersionAsync(CancellationToken cancellationToken = default) =>
        (await CheckGitAsync(cancellationToken).ConfigureAwait(false)).Detail;

    /// <summary>
    /// The workspaces root is relocatable in Settings, so the clones may live somewhere other than
    /// the built-in default — every workspace-related check must use the effective location.
    /// </summary>
    private async Task<string> GetEffectiveWorkspacesRootAsync(CancellationToken cancellationToken)
    {
        var workspacesOverride = await _settings.GetWorkspacesRootOverrideAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(workspacesOverride) ? ObsyncPaths.WorkspacesRoot : workspacesOverride;
    }

    private async Task<DiagnosticResult> CheckGitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _git.RunAsync(ObsyncPaths.Root, ["--version"], cancellationToken).ConfigureAwait(false);
            return result.Success
                ? new DiagnosticResult("Git CLI", DiagnosticStatus.Pass, $"{result.StandardOutput.Trim()} — {DescribeGitSource()}", _clock.UtcNow)
                : new DiagnosticResult("Git CLI", DiagnosticStatus.Fail, result.StandardError.Trim(), _clock.UtcNow);
        }
        catch (Exception ex)
        {
            // The runner throws when git can't be started (not on PATH).
            return new DiagnosticResult("Git CLI", DiagnosticStatus.Fail, $"git is not available: {ex.Message}", _clock.UtcNow);
        }
    }

    /// <summary>Names which git executable Obsync resolved (see <see cref="GitCommandRunner.GitExecutable"/>).</summary>
    private static string DescribeGitSource()
    {
        var executable = GitCommandRunner.GitExecutable;
        if (executable == "git")
        {
            return "from PATH";
        }

        return executable.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
            ? $"bundled ({executable})"
            : $"OBSYNC_GIT override ({executable})";
    }

    /// <summary>
    /// Round-trips a sentinel credential (write → read → delete) so a broken Credential Manager is
    /// caught here instead of as a silent authentication failure at run time. The sentinel value is
    /// a random GUID and never appears in the result, the logs, or the support bundle.
    /// </summary>
    internal static DiagnosticResult ProbeCredentialStore(ICredentialStore credentials, DateTimeOffset checkedAt)
    {
        const string name = "Credential Manager";
        try
        {
            var sentinel = Guid.NewGuid().ToString("N");
            credentials.Store(CredentialProbeKey, sentinel);
            var readBack = credentials.Retrieve(CredentialProbeKey);
            credentials.Delete(CredentialProbeKey);
            return readBack == sentinel
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, "Secrets can be stored, read, and deleted.", checkedAt)
                : new DiagnosticResult(name, DiagnosticStatus.Fail,
                    "A stored test value did not read back correctly — saved passwords and tokens may not be retrievable. " +
                    "Check that Windows Credential Manager works for this account (Control Panel → Credential Manager).", checkedAt);
        }
        catch (Exception ex)
        {
            try
            {
                credentials.Delete(CredentialProbeKey);
            }
            catch (Exception)
            {
                // Best-effort cleanup of the sentinel; the primary failure is what gets reported.
            }

            return new DiagnosticResult(name, DiagnosticStatus.Fail,
                $"Windows Credential Manager is not usable — {ex.Message} SQL passwords and GitHub tokens cannot be stored or read.", checkedAt);
        }
    }

    /// <summary>Creates and deletes a probe file so permission problems surface before a run fails on them.</summary>
    internal static DiagnosticResult ProbeFolderWritable(string name, string path, DateTimeOffset checkedAt)
    {
        try
        {
            // Creating the folder is part of writability — the app creates these at startup too.
            Directory.CreateDirectory(path);
            var probeFile = Path.Combine(path, $".obsync-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "probe");
            File.Delete(probeFile);
            return new DiagnosticResult(name, DiagnosticStatus.Pass, $"Writable — {path}", checkedAt);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail,
                $"Cannot write to {path} — {ex.Message} Fix the folder's permissions or free up the drive.", checkedAt);
        }
    }

    /// <summary>
    /// Existence + size + a fast <c>PRAGMA quick_check(1)</c> (not the full integrity_check — this
    /// runs on every diagnostics pass). <paramref name="databasePath"/> must be the same file the
    /// <paramref name="connectionFactory"/> opens.
    /// </summary>
    internal static async Task<DiagnosticResult> ProbeStateDatabaseAsync(
        string databasePath, IDbConnectionFactory connectionFactory, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        const string name = "State database";
        try
        {
            var file = new FileInfo(databasePath);
            if (!file.Exists)
            {
                return new DiagnosticResult(name, DiagnosticStatus.Fail,
                    $"The state database was not found at {databasePath}. Restart Obsync to recreate it.", checkedAt);
            }

            var size = StorageUsage.FormatBytes(file.Length);
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            var verdict = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase)
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, $"{size} — quick integrity check passed.", checkedAt)
                : new DiagnosticResult(name, DiagnosticStatus.Warning,
                    $"{size} — quick integrity check reported: {verdict}. Export a support bundle and back up the data folder.", checkedAt);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, ex.Message, checkedAt);
        }
    }

    private DiagnosticResult CheckDiskSpace(string workspacesRoot)
    {
        try
        {
            var drives = new[] { ObsyncPaths.Root, workspacesRoot }
                .Select(Path.GetPathRoot)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (drives.Count == 0)
            {
                return new DiagnosticResult("Disk space", DiagnosticStatus.Warning, "Could not determine the data drive.", _clock.UtcNow);
            }

            var low = false;
            var details = new List<string>();
            foreach (var drive in drives)
            {
                var free = new DriveInfo(drive!).AvailableFreeSpace;
                low |= free < LowDiskThresholdBytes;
                details.Add($"{free / 1024d / 1024 / 1024:0.0} GB free on {drive}");
            }

            var detail = string.Join(" · ", details);
            return low
                ? new DiagnosticResult("Disk space", DiagnosticStatus.Warning, $"Low — {detail}", _clock.UtcNow)
                : new DiagnosticResult("Disk space", DiagnosticStatus.Pass, detail, _clock.UtcNow);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult("Disk space", DiagnosticStatus.Warning, ex.Message, _clock.UtcNow);
        }
    }

    private async Task<DiagnosticResult> CheckSchedulerAsync(CancellationToken cancellationToken)
    {
        // SCM state alone can't answer "will MY schedules run" — the health service also checks the
        // logon account and the heartbeat the service writes into this user's database.
        var health = await _schedulerHealth.GetAsync(cancellationToken).ConfigureAwait(false);
        // Keyed on Healthy, not CanExecuteSchedules: a service running under another account does
        // execute these schedules (so no banner), but its separate credential vault is exactly the
        // kind of thing diagnostics exists to surface.
        return new DiagnosticResult(
            "Obsync service",
            health.Status == SchedulerHealthStatus.Healthy ? DiagnosticStatus.Pass : DiagnosticStatus.Warning,
            health.Summary,
            _clock.UtcNow);
    }

    private async Task<DiagnosticResult> CheckProxyAsync(CancellationToken cancellationToken)
    {
        var resolution = await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            return new DiagnosticResult("Proxy", DiagnosticStatus.Pass, "Direct connection (no proxy).", _clock.UtcNow);
        }

        var result = await _proxy.TestAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new DiagnosticResult("Proxy", DiagnosticStatus.Pass, "GitHub is reachable through the proxy.", _clock.UtcNow)
            : new DiagnosticResult("Proxy", DiagnosticStatus.Fail, result.Error ?? "The proxy test failed.", _clock.UtcNow);
    }

    private async Task<DiagnosticResult> CheckServerAsync(SqlConnectionProfile server, CancellationToken cancellationToken)
    {
        var name = $"SQL · {server.Name}";
        try
        {
            var password = server.RequiresPassword ? _credentials.Retrieve(CredentialKeys.SqlPassword(server.Id)) : null;
            var result = await _probe.TestConnectionAsync(server, password, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, $"{result.Value.Edition} ({result.Value.ProductVersion})", _clock.UtcNow)
                : new DiagnosticResult(name, DiagnosticStatus.Fail, result.Error ?? "Connection failed.", _clock.UtcNow);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message, _clock.UtcNow);
        }
    }

    private async Task<DiagnosticResult> CheckRepositoryAsync(GitRepositoryProfile repository, CancellationToken cancellationToken)
    {
        var name = $"GitHub · {repository.FullName}";

        // Inside the try, unlike every sibling check. It used to sit outside, and the credential
        // store throws for any CredRead error other than "not found" — so on a machine with a broken
        // vault the exception escaped RunAsync and discarded the WHOLE report, including the
        // "Credential Manager: Fail" row that had already been produced to explain it. The one page
        // built to diagnose credential problems showed nothing at all.
        string? token;
        try
        {
            token = _credentials.Retrieve(CredentialKeys.GitHubToken(repository.Id));
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, $"The stored token could not be read — {ex.Message}", _clock.UtcNow);
        }

        if (string.IsNullOrEmpty(token))
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, "No access token stored for this repository.", _clock.UtcNow);
        }

        try
        {
            var result = await _gitHub.CheckRepositoryAccessAsync(
                token, repository.Owner, repository.RepositoryName, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return new DiagnosticResult(name, DiagnosticStatus.Fail, result.Error ?? "The GitHub check could not run.", _clock.UtcNow);
            }

            var report = result.Value;
            return report switch
            {
                { TokenValid: false } => new DiagnosticResult(name, DiagnosticStatus.Fail, report.Detail ?? "The token is invalid.", _clock.UtcNow),
                { RepositoryFound: false } => new DiagnosticResult(name, DiagnosticStatus.Fail, report.Detail ?? "The repository is not accessible.", _clock.UtcNow),
                { CanWrite: false } => new DiagnosticResult(name, DiagnosticStatus.Warning, "Read-only token — pushes will fail (needs Contents: write).", _clock.UtcNow),
                _ => new DiagnosticResult(name, DiagnosticStatus.Pass, $"Read + write (as {report.Login}).", _clock.UtcNow),
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message, _clock.UtcNow);
        }
    }

    /// <summary>
    /// The same repository, reached the way a RUN reaches it: bundled git, configured proxy,
    /// configured TLS backend.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the API check above rather than replacing it. When one passes and
    /// the other fails, the pair IS the diagnosis — an API pass with a git fail is a transport
    /// problem (TLS trust, blocked revocation, proxy), and a git pass with an API fail is a token
    /// scope or SSO problem. Collapsing them into one row would throw that away, and the previous
    /// "Git CLI" check only ran <c>git --version</c>, which proves the binary launches and nothing
    /// about whether it can talk to anything.
    /// </remarks>
    private async Task<DiagnosticResult> CheckRepositoryTransportAsync(
        GitRepositoryProfile repository, CancellationToken cancellationToken)
    {
        var name = $"git · {repository.FullName}";

        // Guarded for the same reason as the API check above: an unreadable vault must produce a row,
        // not take the report down with it.
        string? token;
        try
        {
            token = _credentials.Retrieve(CredentialKeys.GitHubToken(repository.Id));
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, $"The stored token could not be read — {ex.Message}", _clock.UtcNow);
        }

        if (string.IsNullOrEmpty(token))
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, "No access token stored for this repository.", _clock.UtcNow);
        }

        try
        {
            var proxyUrl = (await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false))?.GitProxyUrl;
            var tls = await _settings.GetGitTlsAsync(cancellationToken).ConfigureAwait(false);
            var probe = await _gitRemote.CheckAsync(
                new GitNetworkOptions(
                    repository.EffectiveRemoteUrl, GitHubService.BuildAuthorizationHeader(token),
                    proxyUrl, tls.Backend, tls.CaBundlePath),
                repository.DefaultBranch,
                cancellationToken).ConfigureAwait(false);

            return probe.IsSuccess
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, "git reached the repository and authenticated.", _clock.UtcNow)
                : new DiagnosticResult(name, DiagnosticStatus.Fail, probe.Error ?? "git could not reach the repository.", _clock.UtcNow);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message, _clock.UtcNow);
        }
    }
}
