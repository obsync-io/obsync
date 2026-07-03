using System.IO;
using System.ServiceProcess;
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
public sealed record DiagnosticResult(string Name, DiagnosticStatus Status, string Detail);

/// <summary>Runs the environment health checks surfaced on the Settings → Diagnostics card.</summary>
public interface IDiagnosticsService
{
    Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiagnosticsService" />
public sealed class DiagnosticsService : IDiagnosticsService
{
    private const long LowDiskThresholdBytes = 1L * 1024 * 1024 * 1024; // 1 GB

    private readonly ISqlServerProbe _probe;
    private readonly IGitHubService _gitHub;
    private readonly IGitCommandRunner _git;
    private readonly ICredentialStore _credentials;
    private readonly IConnectionProfileRepository _servers;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IProxyProvider _proxy;

    public DiagnosticsService(
        ISqlServerProbe probe,
        IGitHubService gitHub,
        IGitCommandRunner git,
        ICredentialStore credentials,
        IConnectionProfileRepository servers,
        IRepositoryProfileRepository repositories,
        IProxyProvider proxy)
    {
        _probe = probe;
        _gitHub = gitHub;
        _git = git;
        _credentials = credentials;
        _servers = servers;
        _repositories = repositories;
        _proxy = proxy;
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DiagnosticResult>
        {
            await CheckGitAsync(cancellationToken).ConfigureAwait(false),
            CheckDiskSpace(),
            CheckService(),
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

    private async Task<DiagnosticResult> CheckGitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _git.RunAsync(ObsyncPaths.Root, ["--version"], cancellationToken).ConfigureAwait(false);
            return result.Success
                ? new DiagnosticResult("Git CLI", DiagnosticStatus.Pass, result.StandardOutput.Trim())
                : new DiagnosticResult("Git CLI", DiagnosticStatus.Fail, result.StandardError.Trim());
        }
        catch (Exception ex)
        {
            // The runner throws when git can't be started (not on PATH).
            return new DiagnosticResult("Git CLI", DiagnosticStatus.Fail, $"git is not available: {ex.Message}");
        }
    }

    private static DiagnosticResult CheckDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(ObsyncPaths.Root);
            if (string.IsNullOrEmpty(root))
            {
                return new DiagnosticResult("Disk space", DiagnosticStatus.Warning, "Could not determine the data drive.");
            }

            var free = new DriveInfo(root).AvailableFreeSpace;
            var detail = $"{free / 1024d / 1024 / 1024:0.0} GB free on {root}";
            return free < LowDiskThresholdBytes
                ? new DiagnosticResult("Disk space", DiagnosticStatus.Warning, $"Low — {detail}")
                : new DiagnosticResult("Disk space", DiagnosticStatus.Pass, detail);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult("Disk space", DiagnosticStatus.Warning, ex.Message);
        }
    }

    private static DiagnosticResult CheckService()
    {
        try
        {
            using var controller = new ServiceController("Obsync");
            var status = controller.Status; // throws if the service is not installed
            return status == ServiceControllerStatus.Running
                ? new DiagnosticResult("Obsync service", DiagnosticStatus.Pass, "Running")
                : new DiagnosticResult("Obsync service", DiagnosticStatus.Warning, status.ToString());
        }
        catch (InvalidOperationException)
        {
            // The common case until the Phase 4 installer ships: the service isn't registered.
            return new DiagnosticResult("Obsync service", DiagnosticStatus.Warning,
                "Not installed. Scheduled runs need the Obsync Windows Service; install it to enable them.");
        }
    }

    private async Task<DiagnosticResult> CheckProxyAsync(CancellationToken cancellationToken)
    {
        var resolution = await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            return new DiagnosticResult("Proxy", DiagnosticStatus.Pass, "Direct connection (no proxy).");
        }

        var result = await _proxy.TestAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new DiagnosticResult("Proxy", DiagnosticStatus.Pass, "GitHub is reachable through the proxy.")
            : new DiagnosticResult("Proxy", DiagnosticStatus.Fail, result.Error ?? "The proxy test failed.");
    }

    private async Task<DiagnosticResult> CheckServerAsync(SqlConnectionProfile server, CancellationToken cancellationToken)
    {
        var name = $"SQL · {server.Name}";
        try
        {
            var password = server.RequiresPassword ? _credentials.Retrieve(CredentialKeys.SqlPassword(server.Id)) : null;
            var result = await _probe.TestConnectionAsync(server, password, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, $"{result.Value.Edition} ({result.Value.ProductVersion})")
                : new DiagnosticResult(name, DiagnosticStatus.Fail, result.Error ?? "Connection failed.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message);
        }
    }

    private async Task<DiagnosticResult> CheckRepositoryAsync(GitRepositoryProfile repository, CancellationToken cancellationToken)
    {
        var name = $"GitHub · {repository.FullName}";
        var token = _credentials.Retrieve(CredentialKeys.GitHubToken(repository.Id));
        if (string.IsNullOrEmpty(token))
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, "No access token stored for this repository.");
        }

        try
        {
            var result = await _gitHub.CheckRepositoryAccessAsync(
                token, repository.Owner, repository.RepositoryName, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return new DiagnosticResult(name, DiagnosticStatus.Fail, result.Error ?? "The GitHub check could not run.");
            }

            var report = result.Value;
            return report switch
            {
                { TokenValid: false } => new DiagnosticResult(name, DiagnosticStatus.Fail, report.Detail ?? "The token is invalid."),
                { RepositoryFound: false } => new DiagnosticResult(name, DiagnosticStatus.Fail, report.Detail ?? "The repository is not accessible."),
                { CanWrite: false } => new DiagnosticResult(name, DiagnosticStatus.Warning, "Read-only token — pushes will fail (needs Contents: write)."),
                _ => new DiagnosticResult(name, DiagnosticStatus.Pass, $"Read + write (as {report.Login})."),
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message);
        }
    }
}
