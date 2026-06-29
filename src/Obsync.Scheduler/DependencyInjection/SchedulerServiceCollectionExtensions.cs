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
        return services;
    }
}
