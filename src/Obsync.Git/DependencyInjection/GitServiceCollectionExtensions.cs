using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Obsync.Git.DependencyInjection;

/// <summary>Registers the Git CLI workspace services.</summary>
public static class GitServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncGit(this IServiceCollection services)
    {
        services.TryAddSingleton<IGitCommandRunner, GitCommandRunner>();
        services.TryAddSingleton<IGitWorkspace, GitWorkspace>();
        return services;
    }
}
