using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Obsync.Scheduler.DependencyInjection;

/// <summary>Registers the Obsync job scheduler abstraction over Quartz.</summary>
public static class SchedulerServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncScheduler(this IServiceCollection services)
    {
        services.TryAddSingleton<ISyncJobScheduler, SyncJobScheduler>();
        services.AddTransient<SyncQuartzJob>();

        // Resolved by Quartz's trigger-listener registration in the service host. Singleton to
        // match the repositories it depends on; it holds no per-fire state.
        services.TryAddSingleton<MisfiredOccurrenceListener>();
        return services;
    }
}
