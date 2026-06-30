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

/// <summary>Validates GitHub tokens and reads repository/branch metadata via Octokit.</summary>
public interface IGitHubService
{
    Task<Result<string>> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(string token, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> GetBranchesAsync(string token, string owner, string name, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubService" />
public sealed class GitHubService : IGitHubService
{
    private static readonly ProductHeaderValue Product = new("Obsync");
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(ILogger<GitHubService> logger) => _logger = logger;

    public async Task<Result<string>> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(token);
            var user = await WithRetryAsync(() => client.User.Current(), cancellationToken).ConfigureAwait(false);
            return Result.Success(user.Login);
        }
        catch (AuthorizationException)
        {
            return Result.Failure<string>("The token was rejected by GitHub. Check that it is valid and not expired.");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("GitHub token validation failed: {Message}", ex.Message);
            return Result.Failure<string>($"GitHub error: {ex.Message}");
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
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        return $"AUTHORIZATION: basic {basic}";
    }
}
