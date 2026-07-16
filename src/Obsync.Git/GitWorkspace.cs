using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Obsync.Shared.Results;

namespace Obsync.Git;

/// <summary>Everything needed to operate on a local Git workspace for one repository/branch.</summary>
public sealed class GitWorkspaceContext
{
    public required string RemoteUrl { get; init; }

    /// <summary>The branch that is checked out, committed to, and pushed. In pull-request mode this is
    /// the per-run head branch; in direct-commit mode it is the target branch.</summary>
    public required string Branch { get; init; }

    /// <summary>
    /// When set, pull-request mode: <see cref="Branch"/> is created fresh off this base branch each
    /// run (the base is the PR target and must already exist on the remote). Null = direct-commit mode.
    /// </summary>
    public string? BaseBranch { get; init; }

    public required string LocalPath { get; init; }

    /// <summary>Full HTTP header value used for authentication, e.g. "AUTHORIZATION: basic &lt;base64&gt;". Never logged.</summary>
    public string? AuthorizationHeader { get; init; }

    public string CommitterName { get; init; } = "Obsync";
    public string CommitterEmail { get; init; } = "obsync@localhost";

    /// <summary>Number of attempts (1 = no retry) for transient network operations (clone/fetch/push).</summary>
    public int NetworkRetryCount { get; init; } = 3;

    /// <summary>
    /// HTTP/HTTPS proxy URL for network operations (may embed credentials); null for a direct
    /// connection. Injected per-command via <c>GIT_CONFIG_*</c> environment variables, never
    /// written to <c>.git/config</c>.
    /// </summary>
    public string? ProxyUrl { get; init; }
}

/// <summary>The outcome of a commit attempt.</summary>
public sealed record GitCommitResult(bool HadChanges, string? CommitSha, bool Success, string? Error)
{
    public static GitCommitResult NoChanges() => new(false, null, true, null);
    public static GitCommitResult Committed(string sha) => new(true, sha, true, null);
    public static GitCommitResult Failed(string error) => new(false, null, false, error);
}

/// <summary>Manages a local Git working tree: prepare (clone/fetch/checkout), commit, and push.</summary>
public interface IGitWorkspace
{
    Task<Result> PrepareAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);

    Task<GitCommitResult> CommitAllAsync(
        GitWorkspaceContext context, string subject, string body, bool allowEmpty = false, CancellationToken cancellationToken = default);

    Task<Result> PushAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the local branch has commits that are not yet on the remote — e.g. a previous run
    /// committed but its push failed. Lets the engine re-push instead of losing the work.
    /// </summary>
    Task<bool> HasUnpushedCommitsAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitWorkspace" />
public sealed partial class GitWorkspace : IGitWorkspace
{
    private readonly IGitCommandRunner _git;
    private readonly ILogger<GitWorkspace> _logger;

    public GitWorkspace(IGitCommandRunner git, ILogger<GitWorkspace> logger)
    {
        _git = git;
        _logger = logger;
    }

    public async Task<Result> PrepareAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default)
    {
        var gitDir = Path.Combine(context.LocalPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            var cloned = await CloneFreshAsync(context, cancellationToken).ConfigureAwait(false);
            if (cloned.IsFailure)
            {
                return cloned;
            }
        }
        else
        {
            // A cancellation kills the git process tree mid-command; a kill during add/commit
            // strands .git/index.lock and every later index operation fails with "File exists".
            // Obsync serializes all workspace access via a cross-process per-repository lock, so
            // any lock file that exists here is stale by construction and safe to delete.
            var indexLock = Path.Combine(gitDir, "index.lock");
            if (File.Exists(indexLock))
            {
                _logger.LogWarning("Removing a stale git index.lock left by an interrupted run: {Path}", indexLock);
                File.Delete(indexLock);
            }

            // Only clone consumes RemoteUrl, so an edited repository profile would keep pushing to
            // the old remote forever. Idempotent and local. Best-effort: on a corrupt workspace
            // this fails, and the fetch below detects and heals the corruption.
            _ = await _git.RunAsync(
                context.LocalPath, ["remote", "set-url", "origin", context.RemoteUrl], cancellationToken).ConfigureAwait(false);

            var fetch = await RunNetworkAsync(context.LocalPath, context, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
            if (!fetch.Success)
            {
                // Distinguish "network/auth problem" (surface it) from "the clone itself is broken"
                // (a crash or kill mid-clone corrupts .git) — a broken workspace would otherwise
                // fail every future run until someone manually deletes an internal folder. The
                // workspace is fully regenerable, so delete and clone fresh.
                var healthy = (await _git.RunAsync(context.LocalPath, ["rev-parse", "--git-dir"], cancellationToken)
                    .ConfigureAwait(false)).Success;
                if (healthy)
                {
                    return Result.Failure($"git fetch failed: {Summarize(fetch.StandardError)}");
                }

                _logger.LogWarning(
                    "The git workspace at {Path} is corrupt (likely an interrupted clone); recreating it.",
                    context.LocalPath);
                var recloned = await CloneFreshAsync(context, cancellationToken).ConfigureAwait(false);
                if (recloned.IsFailure)
                {
                    return recloned;
                }
            }
        }

        // Pull request mode: create a fresh per-run head branch at the base branch's tip. The base
        // must already exist on the remote (it's what the PR targets); the per-run head does not, so
        // the direct-mode stranded-commit preservation below does not apply.
        if (context.BaseBranch is not null)
        {
            var baseExists = (await _git.RunAsync(
                context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.BaseBranch}"], cancellationToken)
                .ConfigureAwait(false)).Success;
            if (!baseExists)
            {
                return Result.Failure($"The base branch '{context.BaseBranch}' does not exist on the remote.");
            }

            var headCheckout = await _git.RunAsync(
                context.LocalPath, ["checkout", "-B", context.Branch, $"origin/{context.BaseBranch}"], cancellationToken).ConfigureAwait(false);
            return headCheckout.Success
                ? Result.Success()
                : Result.Failure($"git checkout failed: {Summarize(headCheckout.StandardError)}");
        }

        var remoteBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;

        var localBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/heads/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;

        // If the local branch already carries commits that never reached the remote (a prior push
        // failed), DO NOT hard-reset to origin — that would silently discard the committed changes.
        // Just make sure we're on the branch; the engine will re-push the pending commit(s).
        if (localBranchExists
            && await AheadCountAsync(context.LocalPath, context.Branch, remoteBranchExists, cancellationToken).ConfigureAwait(false) > 0)
        {
            var stay = await _git.RunAsync(context.LocalPath, ["checkout", context.Branch], cancellationToken).ConfigureAwait(false);
            return stay.Success
                ? Result.Success()
                : Result.Failure($"git checkout failed: {Summarize(stay.StandardError)}");
        }

        // Otherwise sync the local branch to origin (picking up any remote changes and healing
        // drift). Nothing local is precious on this path — no unpushed commits, and the working
        // tree is regenerated from SQL Server every run — so sync forcefully: a crashed run can
        // leave modified tracked files that a plain checkout refuses to overwrite when the remote
        // advanced ("would be overwritten"), and untracked residue that would otherwise leak into
        // the next commit.
        if (remoteBranchExists)
        {
            var checkout = await _git.RunAsync(
                context.LocalPath, ["checkout", "-f", "-B", context.Branch, $"origin/{context.Branch}"], cancellationToken)
                .ConfigureAwait(false);
            if (!checkout.Success)
            {
                return Result.Failure($"git checkout failed: {Summarize(checkout.StandardError)}");
            }

            var clean = await _git.RunAsync(context.LocalPath, ["clean", "-fd"], cancellationToken).ConfigureAwait(false);
            return clean.Success
                ? Result.Success()
                : Result.Failure($"git clean failed: {Summarize(clean.StandardError)}");
        }

        // No remote branch yet (first run against an empty remote): HEAD may be unborn, so a
        // forced checkout/clean has nothing to sync against.
        var create = await _git.RunAsync(context.LocalPath, ["checkout", "-B", context.Branch], cancellationToken).ConfigureAwait(false);
        return create.Success
            ? Result.Success()
            : Result.Failure($"git checkout failed: {Summarize(create.StandardError)}");
    }

    /// <summary>
    /// Deletes any remnant at the workspace path (a cancelled or killed clone leaves a partial
    /// directory that makes every later clone fail with "destination path already exists") and
    /// clones fresh. Everything in the workspace is regenerable — from the remote and from SQL
    /// Server — so deletion is always safe here.
    /// </summary>
    private async Task<Result> CloneFreshAsync(GitWorkspaceContext context, CancellationToken cancellationToken)
    {
        if (Directory.Exists(context.LocalPath))
        {
            DeleteDirectory(context.LocalPath);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(context.LocalPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var clone = await RunNetworkAsync(parent ?? ".", context, ["clone", context.RemoteUrl, context.LocalPath], cancellationToken)
            .ConfigureAwait(false);
        if (!clone.Success)
        {
            return Result.Failure($"git clone failed: {Summarize(clone.StandardError)}");
        }

        // Windows MAX_PATH protection: a deep workspaces root plus a long schema/object path can
        // exceed 260 chars, failing checkouts and `git show` with "Filename too long". Best-effort
        // (a failure here surfaces later with git's own clear message).
        _ = await _git.RunAsync(
            context.LocalPath, ["config", "core.longpaths", "true"], cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    // Git object files are read-only, which makes Directory.Delete(recursive) throw — clear the
    // attribute first.
    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    public async Task<bool> HasUnpushedCommitsAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default)
    {
        var remoteBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;
        return await AheadCountAsync(context.LocalPath, context.Branch, remoteBranchExists, cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Number of local commits on the branch that are not yet on origin.</summary>
    private async Task<int> AheadCountAsync(string localPath, string branch, bool remoteBranchExists, CancellationToken cancellationToken)
    {
        // With no remote branch every local commit is un-pushed; otherwise count origin/branch..branch.
        var range = remoteBranchExists ? $"origin/{branch}..{branch}" : branch;
        var result = await _git.RunAsync(localPath, ["rev-list", "--count", range], cancellationToken).ConfigureAwait(false);
        return result.Success && int.TryParse(result.StandardOutput.Trim(), out var count) ? count : 0;
    }

    public async Task<GitCommitResult> CommitAllAsync(
        GitWorkspaceContext context, string subject, string body, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        // untrackedCache persists untracked-directory mtimes in the index, so the add/status walks
        // over a large exported tree (a VLDB run writes 100k+ script files) skip unchanged directories
        // on subsequent runs. The exclude pathspec keeps the engine's atomic-write temp files
        // (*.obsync-tmp, orphaned only by a hard process kill mid-write) out of commits.
        var add = await _git.RunAsync(
            context.LocalPath,
            ["-c", "core.untrackedCache=true", "add", "-A", "--", ".", ":(exclude,glob)**/*.obsync-tmp"],
            cancellationToken).ConfigureAwait(false);
        if (!add.Success)
        {
            return GitCommitResult.Failed($"git add failed: {Summarize(add.StandardError)}");
        }

        var status = await _git.RunAsync(
            context.LocalPath,
            ["-c", "core.untrackedCache=true", "status", "--porcelain", "--", ".", ":(exclude,glob)**/*.obsync-tmp"],
            cancellationToken).ConfigureAwait(false);
        var hasWorkingChanges = !string.IsNullOrWhiteSpace(status.StandardOutput);
        if (!hasWorkingChanges && !allowEmpty)
        {
            return GitCommitResult.NoChanges();
        }

        var commitArgs = new List<string>
        {
            "-c", $"user.name={context.CommitterName}",
            "-c", $"user.email={context.CommitterEmail}",
            "commit", "-m", subject, "-m", body,
        };
        if (!hasWorkingChanges)
        {
            commitArgs.Add("--allow-empty");
        }

        var commit = await _git.RunAsync(context.LocalPath, commitArgs, cancellationToken).ConfigureAwait(false);
        if (!commit.Success)
        {
            return GitCommitResult.Failed($"git commit failed: {Summarize(commit.StandardError)}");
        }

        var rev = await _git.RunAsync(context.LocalPath, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        var sha = rev.StandardOutput.Trim();
        _logger.LogInformation("Created commit {Sha} on {Branch}.", sha, context.Branch);
        return GitCommitResult.Committed(sha);
    }

    public async Task<Result> PushAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default)
    {
        var push = await RunNetworkAsync(
            context.LocalPath, context, ["push", "-u", "origin", context.Branch], cancellationToken).ConfigureAwait(false);
        return push.Success
            ? Result.Success()
            : Result.Failure($"git push failed: {Summarize(push.StandardError)}");
    }

    private async Task<GitCommandResult> RunNetworkAsync(
        string workingDirectory, GitWorkspaceContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        // Secrets travel as GIT_CONFIG_* ENVIRONMENT variables, never as -c command-line arguments:
        // Windows process-creation auditing (Event 4688, Sysmon, EDR) records child command lines
        // verbatim into machine-wide security logs, which would capture the token and any proxy
        // credentials; environment blocks are not captured. Nothing is written to .git/config.
        var environment = new Dictionary<string, string>();
        void AddConfig(string key, string value)
        {
            var index = environment.Count / 2;
            environment[$"GIT_CONFIG_KEY_{index}"] = key;
            environment[$"GIT_CONFIG_VALUE_{index}"] = value;
        }

        if (!string.IsNullOrEmpty(context.AuthorizationHeader))
        {
            AddConfig("http.extraheader", context.AuthorizationHeader);

            // Authenticate with ONLY the injected header. Disable any configured credential helper
            // (e.g. Git Credential Manager, which is on by default on Windows): otherwise git can
            // override or race our header, or block trying to prompt — the usual reason a push that
            // should succeed fails with "could not read Username" / "Authentication failed".
            AddConfig("credential.helper", string.Empty);
        }

        // Route network operations through the configured proxy (may carry credentials).
        if (!string.IsNullOrEmpty(context.ProxyUrl))
        {
            AddConfig("http.proxy", context.ProxyUrl);
        }

        if (environment.Count > 0)
        {
            environment["GIT_CONFIG_COUNT"] = (environment.Count / 2).ToString();
        }

        var maxAttempts = Math.Max(1, context.NetworkRetryCount);
        var attempt = 0;
        while (true)
        {
            attempt++;
            var result = await _git.RunAsync(workingDirectory, args, environment, cancellationToken).ConfigureAwait(false);
            if (result.Success || attempt >= maxAttempts || !GitTransientErrors.IsTransient(result.StandardError))
            {
                return result;
            }

            _logger.LogWarning(
                "Transient git network failure on '{Op}' (attempt {Attempt}/{Max}); retrying.",
                args.Count > 0 ? args[0] : "?", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Condenses git stderr into persistable failure text. These strings outlive the run
    /// (runs.error_message, run logs, reports, support bundles), and git/curl echo URLs verbatim —
    /// a manual proxy URL may embed <c>user:password@host</c> — so URL userinfo is redacted first.
    /// Internal for tests.
    /// </summary>
    internal static string Summarize(string error)
    {
        var redacted = UrlCredentials().Replace(error.Trim(), "://***@");
        return redacted.Length <= 500 ? redacted : redacted[..500] + "…";
    }

    [GeneratedRegex(@"://[^/:@\s]+:[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
}
