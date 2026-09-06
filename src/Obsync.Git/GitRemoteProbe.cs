using Microsoft.Extensions.Logging;
using Obsync.Shared;
using Obsync.Shared.Results;

namespace Obsync.Git;

/// <summary>
/// Proves that git — the real bundled binary, over the real transport — can reach and authenticate
/// to a remote, without cloning anything.
/// </summary>
/// <remarks>
/// Every "is this repository reachable?" surface in Obsync used to answer with Octokit over .NET's
/// <c>HttpClient</c> against <c>api.github.com</c>, while runs use MinGit over schannel against
/// <c>github.com</c>. Different binary, different TLS stack, different host, different proxy
/// plumbing — so the checks were structurally incapable of catching the failures that actually stop
/// a run. The decisive one: <c>HttpClientHandler.CheckCertificateRevocationList</c> defaults to
/// <c>false</c> and git-for-Windows defaults <c>http.schannelCheckRevoke=true</c>, so a firewall
/// blocking the CA's revocation responder leaves the API green and kills every clone.
/// <para>
/// <c>ls-remote</c> is the whole check: it performs the TLS handshake, sends the auth header, and
/// reads the ref advertisement — then stops. No objects, no working tree, no writes.
/// </para>
/// </remarks>
public interface IGitRemoteProbe
{
    /// <param name="branch">When given, also asserts the branch exists on the remote.</param>
    Task<Result> CheckAsync(
        GitNetworkOptions options, string? branch = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitRemoteProbe" />
public sealed class GitRemoteProbe : IGitRemoteProbe
{
    private readonly IGitCommandRunner _git;
    private readonly ILogger<GitRemoteProbe> _logger;

    public GitRemoteProbe(IGitCommandRunner git, ILogger<GitRemoteProbe> logger)
    {
        _git = git;
        _logger = logger;
    }

    public async Task<Result> CheckAsync(
        GitNetworkOptions options, string? branch = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.RemoteUrl))
        {
            return Result.Failure("No repository URL to check.");
        }

        // Identical construction to a real clone/fetch/push (see GitWorkspace.RunNetworkAsync), which
        // is the only reason this result means anything.
        var environment = GitNetworkEnvironment.Build(options);

        var args = new List<string> { "ls-remote", "--heads", "--", options.RemoteUrl! };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add($"refs/heads/{branch}");
        }

        // ls-remote needs no repository, but git still needs somewhere to start. The temp folder is
        // deliberate: running inside an unrelated working tree would let that repository's config
        // and .git discovery influence the answer.
        var workingDirectory = Path.GetTempPath();

        GitCommandResult result;
        try
        {
            result = await _git.RunAsync(workingDirectory, args, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing git binary throws rather than returning a failed result.
            _logger.LogWarning(ex, "The git remote probe could not start git.");
            return Result.Failure($"Obsync could not run its bundled git: {ex.Message}");
        }

        if (result.Success)
        {
            if (string.IsNullOrWhiteSpace(branch) || !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return Result.Success();
            }

            // Exit 0 with no matching ref: reachable and authenticated, but the branch is not there.
            return Result.Failure(
                $"git reached the repository, but it has no branch named '{branch}'.");
        }

        var stderr = SecretRedactor.Scrub(result.StandardError) ?? string.Empty;
        return Result.Failure(GitTransportDiagnosis.Explain(stderr));
    }
}
