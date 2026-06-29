using Microsoft.Extensions.Logging;
using Obsync.Shared.Results;

namespace Obsync.Git;

/// <summary>Everything needed to operate on a local Git workspace for one repository/branch.</summary>
public sealed class GitWorkspaceContext
{
    public required string RemoteUrl { get; init; }
    public required string Branch { get; init; }
    public required string LocalPath { get; init; }

    /// <summary>Full HTTP header value used for authentication, e.g. "AUTHORIZATION: basic &lt;base64&gt;". Never logged.</summary>
    public string? AuthorizationHeader { get; init; }

    public string CommitterName { get; init; } = "Obsync";
    public string CommitterEmail { get; init; } = "obsync@localhost";
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
    Task<GitCommitResult> CommitAllAsync(GitWorkspaceContext context, string subject, string body, CancellationToken cancellationToken = default);
    Task<Result> PushAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);
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

        var remoteBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;

        var checkoutArgs = remoteBranchExists
            ? new[] { "checkout", "-B", context.Branch, $"origin/{context.Branch}" }
            : ["checkout", "-B", context.Branch];

        var checkout = await _git.RunAsync(context.LocalPath, checkoutArgs, cancellationToken).ConfigureAwait(false);
        return checkout.Success
            ? Result.Success()
            : Result.Failure($"git checkout failed: {Summarize(checkout.StandardError)}");
    }

    public async Task<GitCommitResult> CommitAllAsync(
        GitWorkspaceContext context, string subject, string body, CancellationToken cancellationToken = default)
    {
        var add = await _git.RunAsync(context.LocalPath, ["add", "-A"], cancellationToken).ConfigureAwait(false);
        if (!add.Success)
        {
            return GitCommitResult.Failed($"git add failed: {Summarize(add.StandardError)}");
        }

        var status = await _git.RunAsync(context.LocalPath, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return GitCommitResult.NoChanges();
        }

        var commit = await _git.RunAsync(
            context.LocalPath,
            [
                "-c", $"user.name={context.CommitterName}",
                "-c", $"user.email={context.CommitterEmail}",
                "commit", "-m", subject, "-m", body,
            ],
            cancellationToken).ConfigureAwait(false);
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

    private Task<GitCommandResult> RunNetworkAsync(
        string workingDirectory, GitWorkspaceContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        // Authentication is injected per-command via http.extraheader so the token is never
        // written to .git/config and never persisted on disk.
        var full = new List<string>();
        if (!string.IsNullOrEmpty(context.AuthorizationHeader))
        {
            full.Add("-c");
            full.Add($"http.extraheader={context.AuthorizationHeader}");
        }

        full.AddRange(args);
        return _git.RunAsync(workingDirectory, full, cancellationToken);
    }

    private static string Summarize(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }
}
