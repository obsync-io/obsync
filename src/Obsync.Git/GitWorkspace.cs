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
    /// connection. Injected per-command via <c>-c http.proxy=…</c>, never written to <c>.git/config</c>.
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
public sealed class GitWorkspace : IGitWorkspace
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
        }
        else
        {
            var fetch = await RunNetworkAsync(context.LocalPath, context, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
            if (!fetch.Success)
            {
                return Result.Failure($"git fetch failed: {Summarize(fetch.StandardError)}");
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

        // Otherwise sync the local branch to origin (picking up any remote changes and healing drift).
        var checkoutArgs = remoteBranchExists
            ? new[] { "checkout", "-B", context.Branch, $"origin/{context.Branch}" }
            : ["checkout", "-B", context.Branch];

        var checkout = await _git.RunAsync(context.LocalPath, checkoutArgs, cancellationToken).ConfigureAwait(false);
        return checkout.Success
            ? Result.Success()
            : Result.Failure($"git checkout failed: {Summarize(checkout.StandardError)}");
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
        var add = await _git.RunAsync(context.LocalPath, ["add", "-A"], cancellationToken).ConfigureAwait(false);
        if (!add.Success)
        {
            return GitCommitResult.Failed($"git add failed: {Summarize(add.StandardError)}");
        }

        var status = await _git.RunAsync(context.LocalPath, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
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
        // Authentication is injected per-command via http.extraheader so the token is never
        // written to .git/config and never persisted on disk.
        var full = new List<string>();
        if (!string.IsNullOrEmpty(context.AuthorizationHeader))
        {
            full.Add("-c");
            full.Add($"http.extraheader={context.AuthorizationHeader}");

            // Authenticate with ONLY the injected header. Disable any configured credential helper
            // (e.g. Git Credential Manager, which is on by default on Windows): otherwise git can
            // override or race our header, or block trying to prompt — the usual reason a push that
            // should succeed fails with "could not read Username" / "Authentication failed".
            full.Add("-c");
            full.Add("credential.helper=");
        }

        // Route network operations through the configured proxy (may carry credentials); injected
        // per-command, never written to .git/config.
        if (!string.IsNullOrEmpty(context.ProxyUrl))
        {
            full.Add("-c");
            full.Add($"http.proxy={context.ProxyUrl}");
        }

        full.AddRange(args);

        var maxAttempts = Math.Max(1, context.NetworkRetryCount);
        var attempt = 0;
        while (true)
        {
            attempt++;
            var result = await _git.RunAsync(workingDirectory, full, cancellationToken).ConfigureAwait(false);
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

    private static string Summarize(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }
}
