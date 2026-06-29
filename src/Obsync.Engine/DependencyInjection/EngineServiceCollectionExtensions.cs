using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Obsync.Data.DependencyInjection;
using Obsync.Git.DependencyInjection;
using Obsync.GitHub.DependencyInjection;
using Obsync.Metadata.DependencyInjection;
using Obsync.Shared.DependencyInjection;
using Obsync.Smo.DependencyInjection;

namespace Obsync.Engine.DependencyInjection;

/// <summary>Registers the sync orchestration engine and its Shared utility dependencies.</summary>
public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncEngine(this IServiceCollection services, Action<ObsyncEngineOptions> configure)
    {
        services.AddObsyncShared();
        services.Configure(configure);
        services.TryAddSingleton<ISyncEngine, SyncEngine>();
        return services;
    }

    /// <summary>
    /// Wires every engine-side layer (state database, metadata + SMO providers, Git, GitHub, and
    /// the orchestrator) in one call. Hosts add platform pieces (Security, logging) separately.
    /// </summary>
    public static IServiceCollection AddObsyncCore(
        this IServiceCollection services, string databasePath, Action<ObsyncEngineOptions> configureEngine)
    {
        services.AddObsyncData(databasePath);
        services.AddObsyncMetadata();
        services.AddObsyncSmo();
        services.AddObsyncGit();
        services.AddObsyncGitHub();
        services.AddObsyncEngine(configureEngine);
        return services;
    }
}
