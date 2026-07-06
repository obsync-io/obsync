using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Obsync.Data.Repositories;

namespace Obsync.Data.DependencyInjection;

/// <summary>Registers the local SQLite state database, repositories, and migration runner.</summary>
public static class DataServiceCollectionExtensions
{
    private static bool _dapperConfigured;

    public static IServiceCollection AddObsyncData(this IServiceCollection services, string databasePath)
    {
        ConfigureDapper();

        services.Configure<ObsyncDataOptions>(o => o.DatabasePath = databasePath);

        services.TryAddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.TryAddSingleton<IDatabaseInitializer, DatabaseInitializer>();

        services.TryAddSingleton<IConnectionProfileRepository, ConnectionProfileRepository>();
        services.TryAddSingleton<IRepositoryProfileRepository, RepositoryProfileRepository>();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<IRunRepository, RunRepository>();
        services.TryAddSingleton<IObjectStateRepository, ObjectStateRepository>();
        services.TryAddSingleton<IScriptingWatermarkRepository, ScriptingWatermarkRepository>();
        services.TryAddSingleton<IAppSettingsRepository, AppSettingsRepository>();

        // Resolves the configured proxy for outbound GitHub calls; used by GitHubService + the engine.
        // (Depends on ICredentialStore from AddObsyncSecurity, which both composition roots register.)
        services.TryAddSingleton<Obsync.Shared.Abstractions.IProxyProvider, ProxyProvider>();

        return services;
    }

    private static void ConfigureDapper()
    {
        if (_dapperConfigured)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
        _dapperConfigured = true;
    }
}
