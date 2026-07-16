using System.IO;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>The slice of wizard state the Review-step preflight needs to verify a job draft.</summary>
public sealed record JobPreflightRequest(
    SqlConnectionProfile? Connection,
    GitRepositoryProfile? Repository,
    string Branch,
    CommitMode CommitMode,
    string? ExportPath,
    string EffectiveFolder,
    Guid? EditingJobId);

/// <summary>
/// Runs the optional pre-save checks on the wizard's Review step: SQL connectivity, repository
/// access and branch existence, export-destination writability, credential presence, and folder
/// collisions. Purely advisory — results never block saving the job.
/// </summary>
public interface IJobPreflightService
{
    Task<IReadOnlyList<DiagnosticResult>> RunAsync(JobPreflightRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IJobPreflightService" />
public sealed class JobPreflightService : IJobPreflightService
{
    private readonly ISqlServerProbe _probe;
    private readonly IGitHubService _gitHub;
    private readonly ICredentialStore _credentials;
    private readonly IJobRepository _jobs;

    public JobPreflightService(ISqlServerProbe probe, IGitHubService gitHub, ICredentialStore credentials, IJobRepository jobs)
    {
        _probe = probe;
        _gitHub = gitHub;
        _credentials = credentials;
        _jobs = jobs;
    }

    /// <summary>
    /// Finds another job that writes to the same repository + effective folder (case-insensitive) —
    /// two such jobs would overwrite each other's files on every run. Shared with the wizard's live
    /// Destination-step warning so both surfaces always agree.
    /// </summary>
    public static SyncJob? FindFolderCollision(
        IEnumerable<SyncJob> jobs, Guid repositoryId, string effectiveFolder, Guid? excludeJobId) =>
        jobs.FirstOrDefault(job => job.Id != excludeJobId
            && job.RepositoryProfileId == repositoryId
            && string.Equals(job.DestinationFolder, effectiveFolder, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
        JobPreflightRequest request, CancellationToken cancellationToken = default)
    {
        // Sequential on purpose: each check is cheap, and one shared SQL/GitHub outage produces an
        // ordered, readable list instead of a burst of parallel failures.
        var results = new List<DiagnosticResult> { await CheckSqlAsync(request, cancellationToken).ConfigureAwait(false) };

        if (request.CommitMode == CommitMode.ExportOnly)
        {
            results.Add(CheckExportDestination(request.ExportPath));
        }
        else
        {
            results.AddRange(await CheckRepositoryAsync(request, cancellationToken).ConfigureAwait(false));
        }

        results.Add(CheckCredentials(request));

        if (request.CommitMode != CommitMode.ExportOnly && request.Repository is not null)
        {
            results.Add(await CheckFolderCollisionAsync(request, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<DiagnosticResult> CheckSqlAsync(JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "SQL connection";
        if (request.Connection is null)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, "Select a server on the Source step first.");
        }

        try
        {
            var password = request.Connection.RequiresPassword
                ? _credentials.Retrieve(CredentialKeys.SqlPassword(request.Connection.Id))
                : null;
            var result = await _probe.TestConnectionAsync(request.Connection, password, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, $"{result.Value.Edition} ({result.Value.ProductVersion})")
                : new DiagnosticResult(name, DiagnosticStatus.Fail, result.Error ?? "Connection failed.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message);
        }
    }

    private async Task<IReadOnlyList<DiagnosticResult>> CheckRepositoryAsync(
        JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "Repository access";
        if (request.Repository is null)
        {
            return [new DiagnosticResult(name, DiagnosticStatus.Fail, "Select a destination repository on the Destination step first.")];
        }

        var token = _credentials.Retrieve(CredentialKeys.GitHubToken(request.Repository.Id));
        if (string.IsNullOrEmpty(token))
        {
            return [new DiagnosticResult(name, DiagnosticStatus.Warning, "Skipped — no GitHub token stored for this repository.")];
        }

        try
        {
            var access = await _gitHub.CheckRepositoryAccessAsync(
                token, request.Repository.Owner, request.Repository.RepositoryName, cancellationToken).ConfigureAwait(false);
            if (access.IsFailure)
            {
                return [new DiagnosticResult(name, DiagnosticStatus.Fail, access.Error ?? "The GitHub check could not run.")];
            }

            var report = access.Value;
            if (!report.TokenValid)
            {
                return [new DiagnosticResult(name, DiagnosticStatus.Fail, report.Detail ?? "The token is invalid.")];
            }

            if (!report.RepositoryFound)
            {
                return [new DiagnosticResult(name, DiagnosticStatus.Fail, report.Detail ?? "The repository is not accessible.")];
            }

            // Mode-aware verdict: Local Commit Only never pushes, so a read-only token is fine there.
            var accessResult = report switch
            {
                { CanWrite: true } => new DiagnosticResult(name, DiagnosticStatus.Pass, $"Read + write (as {report.Login})."),
                _ when request.CommitMode == CommitMode.LocalCommitOnly =>
                    new DiagnosticResult(name, DiagnosticStatus.Pass, $"Read-only (as {report.Login}) — sufficient for local commits."),
                _ => new DiagnosticResult(name, DiagnosticStatus.Warning, "Read-only token — pushes will fail (needs Contents: write)."),
            };
            return [accessResult, await CheckBranchAsync(token, request, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex)
        {
            return [new DiagnosticResult(name, DiagnosticStatus.Fail, ex.Message)];
        }
    }

    private async Task<DiagnosticResult> CheckBranchAsync(
        string token, JobPreflightRequest request, CancellationToken cancellationToken)
    {
        var name = $"Branch '{request.Branch}'";
        try
        {
            var branches = await _gitHub.GetBranchesAsync(
                token, request.Repository!.Owner, request.Repository.RepositoryName, cancellationToken).ConfigureAwait(false);
            if (branches.IsFailure)
            {
                return new DiagnosticResult(name, DiagnosticStatus.Warning, branches.Error ?? "Could not list the remote branches.");
            }

            if (branches.Value.Contains(request.Branch, StringComparer.Ordinal))
            {
                return new DiagnosticResult(name, DiagnosticStatus.Pass, "The branch exists on the remote.");
            }

            // A missing branch is fatal only for PR mode (the engine refuses a missing base branch);
            // direct/local commits create it with checkout -B and it appears on the first push.
            return request.CommitMode == CommitMode.PullRequest
                ? new DiagnosticResult(name, DiagnosticStatus.Fail, "The pull-request base branch does not exist on the remote.")
                : new DiagnosticResult(name, DiagnosticStatus.Warning, "Not found on the remote — it is created on the first push.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, ex.Message);
        }
    }

    private static DiagnosticResult CheckExportDestination(string? exportPath)
    {
        const string name = "Export destination";
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, "Enter an export destination on the Destination step first.");
        }

        try
        {
            // A .zip destination is written as a file — probe its parent folder instead.
            var directory = exportPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(exportPath)
                : exportPath;
            if (string.IsNullOrEmpty(directory))
            {
                return new DiagnosticResult(name, DiagnosticStatus.Fail, "The export destination has no parent folder.");
            }

            Directory.CreateDirectory(directory);
            var probeFile = Path.Combine(directory, $".obsync-preflight-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "probe");
            File.Delete(probeFile);
            return new DiagnosticResult(name, DiagnosticStatus.Pass, $"{directory} is writable.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Fail, $"Not writable — {ex.Message}");
        }
    }

    private DiagnosticResult CheckCredentials(JobPreflightRequest request)
    {
        const string name = "Credentials";
        try
        {
            var missing = new List<string>();
            if (request.Connection is { RequiresPassword: true } connection
                && !_credentials.Exists(CredentialKeys.SqlPassword(connection.Id)))
            {
                missing.Add("SQL login password");
            }

            if (request.CommitMode != CommitMode.ExportOnly && request.Repository is { } repository
                && !_credentials.Exists(CredentialKeys.GitHubToken(repository.Id)))
            {
                missing.Add("GitHub access token");
            }

            return missing.Count == 0
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, "All required secrets are stored in Windows Credential Manager.")
                : new DiagnosticResult(name, DiagnosticStatus.Fail, $"Missing from Windows Credential Manager: {string.Join(", ", missing)}.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, ex.Message);
        }
    }

    private async Task<DiagnosticResult> CheckFolderCollisionAsync(
        JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "Folder collision";
        try
        {
            var jobs = await _jobs.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var other = FindFolderCollision(jobs, request.Repository!.Id, request.EffectiveFolder, request.EditingJobId);
            return other is null
                ? new DiagnosticResult(name, DiagnosticStatus.Pass, "No other job writes to this repository folder.")
                : new DiagnosticResult(name, DiagnosticStatus.Warning,
                    $"Job '{other.Name}' also writes to this folder — runs will overwrite each other's files.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(name, DiagnosticStatus.Warning, ex.Message);
        }
    }
}
