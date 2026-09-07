using System.IO;
using Obsync.Data.Repositories;
using Obsync.Git;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>The slice of wizard state the Review-step preflight needs to verify a job draft.</summary>
/// <param name="Databases">
/// The databases this job will script. Needed because the permissions a run depends on are granted
/// PER DATABASE, and a connection test opens only the login's default one — so without this the
/// preflight could report a healthy SQL connection for a login that cannot read a single object in
/// any database the job actually targets.
/// </param>
/// <param name="Schedule">
/// The job's cadence, when it has one. Needed because a scheduled job runs in the SERVICE process
/// under a different Windows account than every check here — and credentials and the data folder
/// are per-account, so a green preflight proves nothing about a scheduled run unless the two
/// accounts match.
/// </param>
public sealed record JobPreflightRequest(
    SqlConnectionProfile? Connection,
    GitRepositoryProfile? Repository,
    string Branch,
    CommitMode CommitMode,
    string? ExportPath,
    string EffectiveFolder,
    Guid? EditingJobId,
    IReadOnlyList<string>? Databases = null,
    ScheduleProfile? Schedule = null);

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
    private readonly IClock _clock;
    private readonly IGitRemoteProbe _gitRemote;
    private readonly IProxyProvider _proxy;
    private readonly IAppSettingsRepository _settings;
    private readonly ISchedulerHealthService _schedulerHealth;

    public JobPreflightService(
        ISqlServerProbe probe, IGitHubService gitHub, ICredentialStore credentials, IJobRepository jobs, IClock clock,
        IGitRemoteProbe gitRemote, IProxyProvider proxy, IAppSettingsRepository settings,
        ISchedulerHealthService schedulerHealth)
    {
        _probe = probe;
        _gitHub = gitHub;
        _credentials = credentials;
        _jobs = jobs;
        _clock = clock;
        _gitRemote = gitRemote;
        _proxy = proxy;
        _settings = settings;
        _schedulerHealth = schedulerHealth;
    }

    private DiagnosticResult Result(string name, DiagnosticStatus status, string detail) =>
        new(name, status, detail, _clock.UtcNow);

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
        results.AddRange(await CheckDatabasePermissionsAsync(request, cancellationToken).ConfigureAwait(false));

        if (request.CommitMode == CommitMode.ExportOnly)
        {
            results.Add(CheckExportDestination(request.ExportPath));
        }
        else
        {
            results.AddRange(await CheckRepositoryAsync(request, cancellationToken).ConfigureAwait(false));
            results.Add(await CheckGitTransportAsync(request, cancellationToken).ConfigureAwait(false));
        }

        results.Add(CheckCredentials(request));
        results.Add(await CheckRunIdentityAsync(request, cancellationToken).ConfigureAwait(false));

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
            return Result(name, DiagnosticStatus.Fail, "Select a server on the Source step first.");
        }

        try
        {
            var password = request.Connection.RequiresPassword
                ? _credentials.Retrieve(CredentialKeys.SqlPassword(request.Connection.Id))
                : null;
            var result = await _probe.TestConnectionAsync(request.Connection, password, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? Result(name, DiagnosticStatus.Pass, $"{result.Value.Edition} ({result.Value.ProductVersion})")
                : Result(name, DiagnosticStatus.Fail, result.Error ?? "Connection failed.");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Fail, ex.Message);
        }
    }

    private async Task<IReadOnlyList<DiagnosticResult>> CheckRepositoryAsync(
        JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "Repository access";
        if (request.Repository is null)
        {
            return [Result(name, DiagnosticStatus.Fail, "Select a destination repository on the Destination step first.")];
        }

        var token = _credentials.Retrieve(CredentialKeys.GitHubToken(request.Repository.Id));
        if (string.IsNullOrEmpty(token))
        {
            return [Result(name, DiagnosticStatus.Warning, "Skipped — no GitHub token stored for this repository.")];
        }

        try
        {
            var access = await _gitHub.CheckRepositoryAccessAsync(
                token, request.Repository.Owner, request.Repository.RepositoryName, cancellationToken).ConfigureAwait(false);
            if (access.IsFailure)
            {
                return [Result(name, DiagnosticStatus.Fail, access.Error ?? "The GitHub check could not run.")];
            }

            var report = access.Value;
            if (!report.TokenValid)
            {
                return [Result(name, DiagnosticStatus.Fail, report.Detail ?? "The token is invalid.")];
            }

            if (!report.RepositoryFound)
            {
                return [Result(name, DiagnosticStatus.Fail, report.Detail ?? "The repository is not accessible.")];
            }

            // Mode-aware verdict: Local Commit Only never pushes, so a read-only token is fine there.
            var accessResult = report switch
            {
                { CanWrite: true } => Result(name, DiagnosticStatus.Pass, $"Read + write (as {report.Login})."),
                _ when request.CommitMode == CommitMode.LocalCommitOnly =>
                    Result(name, DiagnosticStatus.Pass, $"Read-only (as {report.Login}) — sufficient for local commits."),
                _ => Result(name, DiagnosticStatus.Warning, "Read-only token — pushes will fail (needs Contents: write)."),
            };
            return [accessResult, await CheckBranchAsync(token, request, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex)
        {
            return [Result(name, DiagnosticStatus.Fail, ex.Message)];
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
                return Result(name, DiagnosticStatus.Warning, branches.Error ?? "Could not list the remote branches.");
            }

            if (branches.Value.Contains(request.Branch, StringComparer.Ordinal))
            {
                return await CheckBranchProtectionAsync(token, request, name, cancellationToken).ConfigureAwait(false);
            }

            // A missing branch is fatal only for PR mode (the engine refuses a missing base branch);
            // direct/local commits create it with checkout -B and it appears on the first push.
            return request.CommitMode == CommitMode.PullRequest
                ? Result(name, DiagnosticStatus.Fail, "The pull-request base branch does not exist on the remote.")
                : Result(name, DiagnosticStatus.Warning, "Not found on the remote — it is created on the first push.");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Warning, ex.Message);
        }
    }

    /// <summary>
    /// The branch exists — but for a mode that pushes to it directly, existing is not the same as
    /// accepting a push.
    /// </summary>
    /// <remarks>
    /// A protected branch leaves <c>permissions.push</c> true and still rejects the push with GH006,
    /// so "Write / push — Contents ✓" was reporting something it had not checked. Only direct-commit
    /// mode is at risk: pull-request mode pushes to its own head branch and merges through review,
    /// which is precisely what protection is asking for. Warning rather than Fail, because a rule
    /// set may well admit this account — the point is that the user is told before the first run
    /// rather than after it.
    /// </remarks>
    private async Task<DiagnosticResult> CheckBranchProtectionAsync(
        string token, JobPreflightRequest request, string name, CancellationToken cancellationToken)
    {
        const string exists = "The branch exists on the remote.";
        if (request.CommitMode != CommitMode.DirectCommit)
        {
            return Result(name, DiagnosticStatus.Pass, exists);
        }

        // Rulesets FIRST. They are GitHub's current mechanism and classic branch protection is the
        // legacy one; they reject with GH013 and GH006 respectively. This check previously looked
        // only at the branch object's `protected` flag, which was designed for the classic system —
        // so a repository governed by a ruleset passed preflight and the run then died on the push.
        // The rules endpoint also names the rule, which a boolean never could.
        var rules = await _gitHub.GetBranchRulesAsync(
            token, request.Repository!.Owner, request.Repository.RepositoryName, request.Branch, cancellationToken)
            .ConfigureAwait(false);

        if (rules.IsSuccess && rules.Value.Count > 0)
        {
            return Result(name, DiagnosticStatus.Warning,
                $"The branch exists but a repository RULESET applies to it ({DescribeRules(rules.Value)}). Direct "
                + "commits are rejected with GitHub error GH013 even though the token has write permission — rules "
                + "are evaluated separately from permissions. Switch the job to Pull request mode, or ask a "
                + "repository administrator for a bypass. Note that a 'required signatures' rule will always reject "
                + "Obsync: its commits are deliberately unsigned.");
        }

        var protection = await _gitHub.IsBranchProtectedAsync(
            token, request.Repository!.Owner, request.Repository.RepositoryName, request.Branch, cancellationToken)
            .ConfigureAwait(false);

        // A failed lookup is not evidence of protection — say the branch exists and leave it there
        // rather than inventing a warning from a network blip. The same applies to the ruleset
        // lookup above: if it failed, this is still consulted, so one endpoint being unavailable
        // never silently downgrades the check to nothing.
        if (protection.IsFailure || !protection.Value)
        {
            return Result(name, DiagnosticStatus.Pass, exists);
        }

        return Result(name, DiagnosticStatus.Warning,
            "The branch exists but is PROTECTED. Direct commits may be rejected (GitHub error GH006) even though the "
            + "token has write permission — protection rules are evaluated separately. Switch the job to Pull request "
            + "mode, or allow this account to push to the branch. Note that a 'require signed commits' rule will "
            + "always reject Obsync: its commits are deliberately unsigned.");
    }

    /// <summary>
    /// Renders GitHub's rule type names as something a person reads, keeping the raw name for
    /// anything this build has not seen — a new rule type must still be reported, not swallowed.
    /// </summary>
    internal static string DescribeRules(IReadOnlyList<string> ruleTypes) =>
        string.Join(", ", ruleTypes.Select(t => t switch
        {
            "pull_request" => "changes must go through a pull request",
            "required_status_checks" => "status checks must pass",
            "required_signatures" => "commits must be signed",
            "required_linear_history" => "linear history required",
            "non_fast_forward" => "force pushes blocked",
            "update" => "branch updates restricted",
            "creation" => "branch creation restricted",
            "deletion" => "branch deletion restricted",
            _ => t,
        }));

    /// <summary>
    /// Proves that GIT can reach and authenticate to the repository — with the bundled binary, the
    /// configured proxy, and the configured TLS backend — rather than inferring it from a REST call.
    /// </summary>
    /// <remarks>
    /// Every other repository check here talks to <c>api.github.com</c> over .NET's
    /// <c>HttpClient</c>; runs talk to <c>github.com</c> over MinGit and schannel. The two disagree
    /// in ways that decide whether a job works: .NET does not check certificate revocation and git
    /// does, .NET reads the Windows trust store and git's OpenSSL backend does not, and the proxy
    /// reaches each of them by a different route. That gap is why this dialog could report five
    /// green ticks immediately before the first run died on <c>git clone</c>.
    /// <para>
    /// <c>ls-remote</c> transfers no objects, so this costs one TLS handshake and a ref listing.
    /// </para>
    /// </remarks>
    private async Task<DiagnosticResult> CheckGitTransportAsync(JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "Git connection";
        if (request.Repository is not { } repository)
        {
            return Result(name, DiagnosticStatus.Fail, "Select a repository on the Destination step first.");
        }

        try
        {
            var token = _credentials.Retrieve(CredentialKeys.GitHubToken(repository.Id));
            if (string.IsNullOrEmpty(token))
            {
                return Result(name, DiagnosticStatus.Fail,
                    "No access token is stored for this repository, so git cannot authenticate.");
            }

            var proxyUrl = (await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false))?.GitProxyUrl;
            var tls = await _settings.GetGitTlsAsync(cancellationToken).ConfigureAwait(false);
            var options = new GitNetworkOptions(
                repository.EffectiveRemoteUrl,
                GitHubService.BuildAuthorizationHeader(token),
                proxyUrl,
                tls.Backend,
                tls.CaBundlePath);

            var probe = await _gitRemote.CheckAsync(options, request.Branch, cancellationToken).ConfigureAwait(false);
            return probe.IsSuccess
                ? Result(name, DiagnosticStatus.Pass, $"git reached {repository.Owner}/{repository.RepositoryName} and authenticated.")
                : Result(name, DiagnosticStatus.Fail, probe.Error ?? "git could not reach the repository.");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Fail, ex.Message);
        }
    }

    /// <summary>How many databases one preflight probes; enough to catch a systemic grant problem.</summary>
    private const int MaxDatabasesProbed = 5;

    /// <summary>
    /// Verifies, per database, the permissions the run actually depends on: CONNECT (by opening the
    /// database by name), and enough visibility to read object definitions.
    /// </summary>
    /// <remarks>
    /// This is the check whose absence let a login without VIEW DEFINITION pass every preflight and
    /// then commit empty scripts over a correct schema — SQL Server reports that loss as NULL
    /// definitions rather than an error, so it is invisible to anything that does not look for it.
    /// Capped at <see cref="MaxDatabasesProbed"/>: a grant problem is virtually always systemic, and
    /// an unbounded probe would open one connection per database on an estate-sized job.
    /// </remarks>
    private async Task<IReadOnlyList<DiagnosticResult>> CheckDatabasePermissionsAsync(
        JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "SQL permissions";
        if (request.Connection is null || request.Databases is not { Count: > 0 } databases)
        {
            return [];
        }

        var probed = databases
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxDatabasesProbed)
            .ToList();
        if (probed.Count == 0)
        {
            return [];
        }

        string? password;
        try
        {
            password = request.Connection.RequiresPassword
                ? _credentials.Retrieve(CredentialKeys.SqlPassword(request.Connection.Id))
                : null;
        }
        catch (Exception ex)
        {
            return [Result(name, DiagnosticStatus.Fail, $"The stored SQL password could not be read — {ex.Message}")];
        }

        var results = new List<DiagnosticResult>();
        var blind = new List<string>();
        var partial = new List<string>();
        var ok = new List<string>();

        foreach (var database in probed)
        {
            try
            {
                var report = await _probe
                    .CheckDatabasePermissionsAsync(request.Connection, password, database, cancellationToken)
                    .ConfigureAwait(false);
                if (report.IsFailure)
                {
                    results.Add(Result(name, DiagnosticStatus.Fail, $"{database}: {report.Error}"));
                    continue;
                }

                var value = report.Value;
                if (value.UnreadableModules > 0)
                {
                    partial.Add($"{database} ({value.UnreadableModules:N0} of {value.Modules:N0})");
                }
                else if (value.VisibleObjects == 0)
                {
                    blind.Add(database);
                }
                else
                {
                    ok.Add(database);
                }
            }
            catch (Exception ex)
            {
                results.Add(Result(name, DiagnosticStatus.Fail, $"{database}: {ex.Message}"));
            }
        }

        if (partial.Count > 0)
        {
            results.Add(Result(name, DiagnosticStatus.Fail,
                $"Object definitions are not readable in {string.Join(", ", partial)}. These objects would be "
                + "committed as EMPTY scripts, overwriting correct ones, and the run would still report success. "
                + "Grant VIEW DEFINITION on the database (Settings → SQL permissions script)."));
        }

        if (blind.Count > 0)
        {
            // Genuinely empty is possible, so this is a warning: only the run itself can tell an
            // empty database from an invisible one, and the mass-deletion safety stop covers that.
            results.Add(Result(name, DiagnosticStatus.Warning,
                $"No user objects are visible in {string.Join(", ", blind)}. If the database is not actually empty, "
                + "the login is missing VIEW DEFINITION and the run would script nothing."));
        }

        if (results.Count == 0 && ok.Count > 0)
        {
            var scope = databases.Count > probed.Count ? $"{probed.Count} of {databases.Count} databases" : "all selected databases";
            results.Add(Result(name, DiagnosticStatus.Pass, $"Definitions are readable in {scope}."));
        }

        return results;
    }

    private DiagnosticResult CheckExportDestination(string? exportPath)
    {
        const string name = "Export destination";
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return Result(name, DiagnosticStatus.Fail, "Enter an export destination on the Destination step first.");
        }

        try
        {
            // A .zip destination is written as a file — probe its parent folder instead.
            var directory = exportPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(exportPath)
                : exportPath;
            if (string.IsNullOrEmpty(directory))
            {
                return Result(name, DiagnosticStatus.Fail, "The export destination has no parent folder.");
            }

            Directory.CreateDirectory(directory);
            var probeFile = Path.Combine(directory, $".obsync-preflight-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "probe");
            File.Delete(probeFile);
            return Result(name, DiagnosticStatus.Pass, $"{directory} is writable.");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Fail, $"Not writable — {ex.Message}");
        }
    }

    /// <summary>
    /// States which Windows account every check above actually ran as, and whether that is the
    /// account a SCHEDULED run will use.
    /// </summary>
    /// <remarks>
    /// This is the check that makes the others honest rather than a check of its own. Everything
    /// preceding it — the SQL connection, the credential presence, the git transport, the folder
    /// probes — executed in the app process under the signed-in user. Scheduled runs execute in the
    /// service process under its own account, and both the Credential Manager vault and
    /// <c>%LOCALAPPDATA%</c> are per-account. So a job could pass 6/6 and then fail every scheduled
    /// run on a missing token, or on <c>Login failed for user 'NT AUTHORITY\SYSTEM'</c> for a login
    /// the preflight never used.
    /// <para>
    /// Reported rather than silently assumed correct: the preflight genuinely CANNOT verify the
    /// other account's vault from here — that is what <c>obsync credential list</c>, run as the
    /// service account, is for — so the honest outcome is to name the gap instead of implying it
    /// was covered.
    /// </para>
    /// </remarks>
    private async Task<DiagnosticResult> CheckRunIdentityAsync(JobPreflightRequest request, CancellationToken cancellationToken)
    {
        const string name = "Run identity";
        var actor = CurrentActor.Name;

        // A manual-only job never leaves this process, so there is no second identity to reconcile.
        if (request.Schedule is not { } schedule || !SchedulerHealthService.NeedsScheduler(
                new SyncJob { Enabled = true, Schedule = schedule }))
        {
            return Result(name, DiagnosticStatus.Pass,
                $"Manual runs only — they execute here, as {actor}, exactly as these checks did.");
        }

        try
        {
            var health = await _schedulerHealth.GetAsync(cancellationToken).ConfigureAwait(false);
            var serviceAccount = health.ServiceAccount;

            if (string.IsNullOrWhiteSpace(serviceAccount))
            {
                return Result(name, DiagnosticStatus.Warning,
                    $"These checks ran as {actor}. Scheduled runs execute in the Obsync service, whose account could "
                    + "not be determined — verify with 'obsync credential list' run as that account.");
            }

            if (string.Equals(serviceAccount, actor, StringComparison.OrdinalIgnoreCase))
            {
                return Result(name, DiagnosticStatus.Pass,
                    $"Scheduled runs execute as {serviceAccount} — the same account these checks used.");
            }

            return Result(name, DiagnosticStatus.Warning,
                $"These checks ran as {actor}, but scheduled runs execute as {serviceAccount}. Credentials and the "
                + "data folder are stored PER ACCOUNT, so nothing above proves a scheduled run will work. Store the "
                + $"secrets for {serviceAccount} by running 'obsync credential set …' as that account, or set the "
                + "service's Log On account to yours (services.msc → Obsync → Log On).");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Warning, $"Could not determine the service's account — {ex.Message}");
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
                ? Result(name, DiagnosticStatus.Pass, "All required secrets are stored in Windows Credential Manager.")
                : Result(name, DiagnosticStatus.Fail, $"Missing from Windows Credential Manager: {string.Join(", ", missing)}.");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Warning, ex.Message);
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
                ? Result(name, DiagnosticStatus.Pass, "No other job writes to this repository folder.")
                : Result(name, DiagnosticStatus.Warning,
                    $"Job '{other.Name}' also writes to this folder — runs will overwrite each other's files.");
        }
        catch (Exception ex)
        {
            return Result(name, DiagnosticStatus.Warning, ex.Message);
        }
    }
}
