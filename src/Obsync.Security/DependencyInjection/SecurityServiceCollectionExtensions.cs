using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Obsync.Shared.Abstractions;

namespace Obsync.Security.DependencyInjection;

/// <summary>Registers Windows-backed secret storage (Credential Manager + DPAPI).</summary>
public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncSecurity(this IServiceCollection services)
    {
        services.TryAddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.TryAddSingleton<ISecretProtector, DpapiSecretProtector>();
        return services;
    }
}
