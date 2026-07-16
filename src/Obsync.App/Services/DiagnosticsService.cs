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
        return new DiagnosticResult(
            "Obsync service",
            health.CanExecuteSchedules ? DiagnosticStatus.Pass : DiagnosticStatus.Warning,
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
        var token = _credentials.Retrieve(CredentialKeys.GitHubToken(repository.Id));
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
}
