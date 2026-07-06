using Microsoft.Extensions.DependencyInjection;
using Obsync.Shared.Scripting;

namespace Obsync.Smo.DependencyInjection;

/// <summary>Registers the SMO high-fidelity script providers (database- and server-level).</summary>
public static class SmoServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncSmo(this IServiceCollection services)
    {
        services.AddSingleton<IObjectScriptProvider, SmoScriptProvider>();
        services.AddSingleton<IServerObjectScriptProvider, SmoServerScriptProvider>();
        return services;
    }
}
