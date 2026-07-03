using Dapper;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Global key/value application settings (proxy configuration, production tag markers).</summary>
public interface IAppSettingsRepository
{
    Task<ProxySettings> GetProxyAsync(CancellationToken cancellationToken = default);
    Task UpsertProxyAsync(ProxySettings settings, CancellationToken cancellationToken = default);

    /// <summary>The tag words that mark a job as production (arms the Run-Now guard). Defaults to <c>prod, production</c>.</summary>
    Task<IReadOnlyList<string>> GetProductionTagsAsync(CancellationToken cancellationToken = default);
    Task SetProductionTagsAsync(IReadOnlyList<string> markers, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAppSettingsRepository" />
public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private const string ProxyKey = "proxy";
    private const string ProductionTagsKey = "productionTags";
    private static readonly IReadOnlyList<string> DefaultProductionTags = ["prod", "production"];

    private readonly IDbConnectionFactory _connectionFactory;

    public AppSettingsRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<ProxySettings> GetProxyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var json = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM app_settings WHERE key = $k;", new { k = ProxyKey }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? new ProxySettings() : ObsyncJson.Deserialize<ProxySettings>(json);
    }

    public async Task UpsertProxyAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            new { k = ProxyKey, v = ObsyncJson.Serialize(settings) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetProductionTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var json = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM app_settings WHERE key = $k;", new { k = ProductionTagsKey }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? DefaultProductionTags : ObsyncJson.Deserialize<List<string>>(json);
    }

    public async Task SetProductionTagsAsync(IReadOnlyList<string> markers, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            new { k = ProductionTagsKey, v = ObsyncJson.Serialize(markers) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
