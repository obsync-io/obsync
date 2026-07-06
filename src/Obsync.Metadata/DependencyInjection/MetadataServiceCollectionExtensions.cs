using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata.DependencyInjection;

/// <summary>Registers the SQL Server metadata reader and fast-path script provider.</summary>
public static class MetadataServiceCollectionExtensions
{
    public static IServiceCollection AddObsyncMetadata(this IServiceCollection services)
    {
        services.TryAddSingleton<ISqlConnectionStringFactory, SqlConnectionStringFactory>();
        services.TryAddSingleton<ISqlServerProbe, SqlServerProbe>();
        services.TryAddSingleton<IDatabaseArtifactReader, DatabaseArtifactReader>();
        services.TryAddSingleton<IReferenceDataReader, ReferenceDataReader>();
        services.TryAddSingleton<IModifiedObjectReader, ModifiedObjectReader>();
        services.AddSingleton<IObjectScriptProvider, MetadataScriptProvider>();
        return services;
    }
}
