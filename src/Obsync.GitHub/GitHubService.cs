using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Results;
using Octokit;
using Octokit.Internal;

namespace Obsync.GitHub;

/// <summary>A GitHub repository visible to the authenticated token.</summary>
public sealed record GitHubRepository(string Owner, string Name, string DefaultBranch, bool Private)
{
    public string FullName => $"{Owner}/{Name}";
}

/// <summary>
/// The outcome of checking a token's effective access to a specific repository. Works for both
/// classic and fine-grained PATs (the repository payload carries the token's <c>pull</c>/<c>push</c>
/// permissions), so it verifies the WRITE access whose absence silently breaks pushes.
/// </summary>
public sealed record TokenPermissionReport(
    bool TokenValid, string? Login, bool RepositoryFound, bool CanRead, bool CanWrite, string? Detail);

/// <summary>An opened pull request. <see cref="ReviewerWarning"/> is set when the PR opened but
/// requesting one or more reviewers failed (a non-fatal condition).</summary>
public sealed record PullRequestInfo(int Number, string HtmlUrl, string? ReviewerWarning);

/// <summary>Checks GitHub tokens, reads repository/branch metadata, and opens pull requests via Octokit.</summary>
public interface IGitHubService
{
    /// <summary>
    /// Verifies the token and its effective access to <paramref name="owner"/>/<paramref name="name"/>:
    /// valid, repository reachable, read, and write. A failed <see cref="Result"/> means the check
    /// itself could not run (e.g. GitHub was unreachable); a successful result carries the checklist.
    /// </summary>
    Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(string token, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> GetBranchesAsync(string token, string owner, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a pull request from <paramref name="headBranch"/> into <paramref name="baseBranch"/> and,
    /// if <paramref name="reviewers"/> is non-empty, requests them (best-effort). A failed
    /// <see cref="Result"/> means the PR itself could not be opened (e.g. missing PR permission).
    /// </summary>
    Task<Result<PullRequestInfo>> CreatePullRequestAsync(
        string token, string owner, string name, string title, string headBranch, string baseBranch,
        string body, IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubService" />
public sealed class GitHubService : IGitHubService
{
    private static readonly ProductHeaderValue Product = new("Obsync");
    private readonly ILogger<GitHubService> _logger;
    private readonly IProxyProvider _proxy;

    public GitHubService(ILogger<GitHubService> logger, IProxyProvider proxy)
    {
        _logger = logger;
        _proxy = proxy;
    }

    public async Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);

            // Identity first: confirms the token is valid at all before probing repository access.
            var user = await WithRetryAsync(() => client.User.Current(), cancellationToken).ConfigureAwait(false);

            // The repository payload carries this token's effective permissions (pull/push/admin),
            // which is the reliable way to verify write access for a fine-grained PAT.
            var repo = await WithRetryAsync(() => client.Repository.Get(owner, name), cancellationToken).ConfigureAwait(false);
            var permissions = repo.Permissions;

            return Result.Success(new TokenPermissionReport(
                TokenValid: true,
                Login: user.Login,
                RepositoryFound: true,
                CanRead: permissions?.Pull ?? false,
                CanWrite: permissions?.Push ?? false,
                Detail: null));
        }
        catch (NotFoundException)
        {
            // The token is valid but cannot see owner/name (no grant to this repository, or a typo).
            return Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: null, RepositoryFound: false, CanRead: false, CanWrite: false,
                Detail: $"The token cannot access {owner}/{name}. Check the name, and that the token grants this repository."));
        }
        catch (AuthorizationException)
        {
            return Result.Success(new TokenPermissionReport(
                TokenValid: false, Login: null, RepositoryFound: false, CanRead: false, CanWrite: false,
                Detail: "The token was rejected by GitHub. Check that it is valid and not expired."));
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("GitHub permission check failed: {Message}", ex.Message);
            return Result.Failure<TokenPermissionReport>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<TokenPermissionReport>($"Could not reach GitHub: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<TokenPermissionReport>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(
        string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);
            var repositories = await WithRetryAsync(
                () => client.Repository.GetAllForCurrent(new RepositoryRequest { Sort = RepositorySort.FullName }),
                cancellationToken).ConfigureAwait(false);

            var mapped = repositories
                .Select(r => new GitHubRepository(r.Owner.Login, r.Name, r.DefaultBranch ?? "main", r.Private))
                .ToList();
            return Result.Success<IReadOnlyList<GitHubRepository>>(mapped);
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<GitHubRepository>>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<GitHubRepository>>($"Could not reach GitHub: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<IReadOnlyList<GitHubRepository>>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<IReadOnlyList<string>>> GetBranchesAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);
            var branches = await WithRetryAsync(() => client.Repository.Branch.GetAll(owner, name), cancellationToken).ConfigureAwait(false);
            return Result.Success<IReadOnlyList<string>>([.. branches.Select(b => b.Name)]);
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"Could not reach GitHub: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<IReadOnlyList<string>>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<PullRequestInfo>> CreatePullRequestAsync(
        string token, string owner, string name, string title, string headBranch, string baseBranch,
        string body, IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);
            var pr = await WithRetryAsync(
                () => client.PullRequest.Create(owner, name, new NewPullRequest(title, headBranch, baseBranch) { Body = body }),
                cancellationToken).ConfigureAwait(false);

            string? reviewerWarning = null;
            if (reviewers.Count > 0)
            {
                try
                {
                    await WithRetryAsync(
                        () => client.PullRequest.ReviewRequest.Create(owner, name, pr.Number, new PullRequestReviewRequest(reviewers, [])),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException
                    || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // The PR is already open; a bad/unknown reviewer or a network blip must not fail it.
                    reviewerWarning = $"The pull request opened, but requesting reviewers failed: {ex.Message}";
                    _logger.LogWarning("Requesting reviewers for PR #{Number} failed: {Message}", pr.Number, ex.Message);
                }
            }

            return Result.Success(new PullRequestInfo(pr.Number, pr.HtmlUrl, reviewerWarning));
        }
        catch (AuthorizationException)
        {
            return Result.Failure<PullRequestInfo>("The token was rejected by GitHub. Check that it is valid and not expired.");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Opening the pull request failed: {Message}", ex.Message);
            return Result.Failure<PullRequestInfo>(ExplainPullRequestFailure(ex));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Opening the pull request failed: {Message}", ex.Message);
            return Result.Failure<PullRequestInfo>($"Could not reach GitHub: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<PullRequestInfo>("The request to GitHub timed out.");
        }
    }

    /// <summary>Turns a PR-create API failure into a short, actionable message.</summary>
    private static string ExplainPullRequestFailure(ApiException ex) => (int)ex.StatusCode switch
    {
        403 => "GitHub denied opening the pull request — the token needs Pull requests: write permission on this repository.",
        422 => $"GitHub could not open the pull request: {ex.Message}", // e.g. a PR already exists, or no diff between base and head
        _ => $"GitHub error opening the pull request: {ex.Message}",
    };

    // Builds a client whose HTTP handler routes through the configured proxy (if any). Resolved per
    // call because the proxy config lives in the DB/Credential Manager and can change at runtime.
    private async Task<GitHubClient> CreateClientAsync(string token, CancellationToken cancellationToken)
    {
        var resolution = await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var adapter = new HttpClientAdapter(() => new HttpClientHandler
        {
            Proxy = resolution?.WebProxy,
            UseProxy = resolution is not null,
        });
        // Trim mirrors BuildAuthorizationHeader: tokens are often pasted with a trailing
        // newline/space, which GitHub rejects as an invalid credential.
        return new GitHubClient(new Connection(Product, adapter)) { Credentials = new Credentials(token.Trim()) };
    }

    /// <summary>
    /// Runs an Octokit call with a short, growing backoff on transient failures (server 5xx,
    /// secondary rate limits, transport blips). Permanent failures — bad credentials, 4xx, the
    /// primary rate limit — are not retried and surface to the caller's catch immediately.
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken, int maxAttempts = 3)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                // The secondary rate limit says exactly how long to back off; retrying sooner just
                // trips it again. Capped at 120s because Retry-After is server-supplied input and
                // must not be able to stall a run indefinitely.
                var delay = ex is AbuseException { RetryAfterSeconds: { } retryAfterSeconds }
                    ? TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, 120))
                    : TimeSpan.FromSeconds(attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        // Primary rate limit resets minutes away — retrying soon cannot help, so surface it.
        RateLimitExceededException => false,
        // Secondary ("abuse") rate limit clears quickly; a short backoff is worthwhile.
        AbuseException => true,
        ApiException api => (int)api.StatusCode >= 500,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>The web URL for a commit, used for the "Open in GitHub" links.</summary>
    public static string BuildCommitUrl(string owner, string name, string sha) =>
        $"https://github.com/{owner}/{name}/commit/{sha}";

    /// <summary>The web URL for a file on a branch, used for the per-change "Open in GitHub" links.</summary>
    public static string BuildBlobUrl(string owner, string name, string branch, string relativePath) =>
        $"https://github.com/{owner}/{name}/blob/{branch}/{relativePath.TrimStart('/')}";

    /// <summary>
    /// Builds the HTTP Authorization header value used to authenticate git over HTTPS with a PAT,
    /// without persisting the token in the repository config.
    /// </summary>
    public static string BuildAuthorizationHeader(string token)
    {
        // Trim: tokens are often pasted with a trailing newline/space, which would corrupt the
        // base64 and cause GitHub to reject every push with an authentication error.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token.Trim()}"));
        return $"AUTHORIZATION: basic {basic}";
    }
}
