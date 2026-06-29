using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Scripting;

namespace Obsync.Shared.DependencyInjection;

/// <summary>Registers the stateless utility services provided by <c>Obsync.Shared</c>.</summary>
public static class SharedServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncShared(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IScriptNormalizer, ScriptNormalizer>();
        services.TryAddSingleton<IObjectHasher, Sha256ObjectHasher>();
        services.TryAddSingleton<IObjectFilePathMapper, ObjectFilePathMapper>();
        return services;
    }
}
