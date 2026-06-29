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
            var user = await client.User.Current().ConfigureAwait(false);
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
            var repositories = await client.Repository.GetAllForCurrent(
                new RepositoryRequest { Sort = RepositorySort.FullName }).ConfigureAwait(false);

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
            var branches = await client.Repository.Branch.GetAll(owner, name).ConfigureAwait(false);
            return Result.Success<IReadOnlyList<string>>([.. branches.Select(b => b.Name)]);
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"GitHub error: {ex.Message}");
        }
    }

    private static GitHubClient CreateClient(string token) =>
        new(Product) { Credentials = new Credentials(token) };

    /// <summary>The web URL for a commit, used for the "Open in GitHub" links.</summary>
    public static string BuildCommitUrl(string owner, string name, string sha) =>
        $"https://github.com/{owner}/{name}/commit/{sha}";

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
