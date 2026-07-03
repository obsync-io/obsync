using Dapper;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Global key/value application settings. Currently the HTTP/HTTPS proxy configuration.</summary>
public interface IAppSettingsRepository
{
    Task<ProxySettings> GetProxyAsync(CancellationToken cancellationToken = default);
    Task UpsertProxyAsync(ProxySettings settings, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAppSettingsRepository" />
public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private const string ProxyKey = "proxy";

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
}
