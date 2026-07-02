using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Obsync.Shared.Results;
using Octokit;

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

/// <summary>Checks GitHub tokens and reads repository/branch metadata via Octokit.</summary>
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
}

/// <inheritdoc cref="IGitHubService" />
public sealed class GitHubService : IGitHubService
{
    private static readonly ProductHeaderValue Product = new("Obsync");
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(ILogger<GitHubService> logger) => _logger = logger;

    public async Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(token);

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
    }

    public async Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(
        string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(token);
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
    }

    public async Task<Result<IReadOnlyList<string>>> GetBranchesAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(token);
            var branches = await WithRetryAsync(() => client.Repository.Branch.GetAll(owner, name), cancellationToken).ConfigureAwait(false);
            return Result.Success<IReadOnlyList<string>>([.. branches.Select(b => b.Name)]);
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"GitHub error: {ex.Message}");
        }
    }

    private static GitHubClient CreateClient(string token) =>
        new(Product) { Credentials = new Credentials(token) };

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
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
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
