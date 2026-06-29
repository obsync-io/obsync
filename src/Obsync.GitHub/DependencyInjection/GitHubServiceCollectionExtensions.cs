using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Obsync.GitHub.DependencyInjection;

/// <summary>Registers the GitHub (Octokit) integration service.</summary>
public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncGitHub(this IServiceCollection services)
    {
        services.TryAddSingleton<IGitHubService, GitHubService>();
        return services;
    }
}
