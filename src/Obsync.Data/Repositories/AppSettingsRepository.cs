using Dapper;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Global key/value application settings (proxy, production tags, retention, committer…).</summary>
public interface IAppSettingsRepository
{
    Task<ProxySettings> GetProxyAsync(CancellationToken cancellationToken = default);
    Task UpsertProxyAsync(ProxySettings settings, CancellationToken cancellationToken = default);

    /// <summary>The tag words that mark a job as production (arms the Run-Now guard). Defaults to <c>prod, production</c>.</summary>
    Task<IReadOnlyList<string>> GetProductionTagsAsync(CancellationToken cancellationToken = default);
    Task SetProductionTagsAsync(IReadOnlyList<string> markers, CancellationToken cancellationToken = default);

    /// <summary>Days of run history to keep. 0 = keep forever (the default).</summary>
    Task<int> GetRunRetentionDaysAsync(CancellationToken cancellationToken = default);
    Task SetRunRetentionDaysAsync(int days, CancellationToken cancellationToken = default);

    /// <summary>The git committer identity for sync commits. Defaults to name "Obsync" with no email
    /// (the engine falls back to its configured default email).</summary>
    Task<CommitterIdentity> GetCommitterAsync(CancellationToken cancellationToken = default);
    Task SetCommitterAsync(CommitterIdentity committer, CancellationToken cancellationToken = default);

    /// <summary>Optional override for where repository clones live. Null/empty = the default
    /// (<c>%LOCALAPPDATA%\Obsync\workspaces</c>). Applies to clones created after the change.</summary>
    Task<string?> GetWorkspacesRootOverrideAsync(CancellationToken cancellationToken = default);
    Task SetWorkspacesRootOverrideAsync(string? path, CancellationToken cancellationToken = default);

    /// <summary>Whether the app shows a notification when a run fails or ends with warnings. Default true.</summary>
    Task<bool> GetNotifyRunFailuresAsync(CancellationToken cancellationToken = default);
    Task SetNotifyRunFailuresAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>When the app last checked for scheduled-run failures (drives the startup summary toast).</summary>
    Task<DateTimeOffset?> GetLastFailureCheckAsync(CancellationToken cancellationToken = default);
    Task SetLastFailureCheckAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAppSettingsRepository" />
public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private const string ProxyKey = "proxy";
    private const string ProductionTagsKey = "productionTags";
    private const string RunRetentionDaysKey = "runRetentionDays";
    private const string CommitterKey = "gitCommitter";
    private const string WorkspacesRootKey = "workspacesRoot";
    private const string NotifyRunFailuresKey = "notifyRunFailures";
    private const string LastFailureCheckKey = "lastFailureCheck";

    private static readonly IReadOnlyList<string> DefaultProductionTags = ["prod", "production"];

    private readonly IDbConnectionFactory _connectionFactory;

    public AppSettingsRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<ProxySettings> GetProxyAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetValueAsync(ProxyKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? new ProxySettings() : ObsyncJson.Deserialize<ProxySettings>(json);
    }

    public Task UpsertProxyAsync(ProxySettings settings, CancellationToken cancellationToken = default) =>
        SetValueAsync(ProxyKey, ObsyncJson.Serialize(settings), cancellationToken);

    public async Task<IReadOnlyList<string>> GetProductionTagsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetValueAsync(ProductionTagsKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? DefaultProductionTags : ObsyncJson.Deserialize<List<string>>(json);
    }

    public Task SetProductionTagsAsync(IReadOnlyList<string> markers, CancellationToken cancellationToken = default) =>
        SetValueAsync(ProductionTagsKey, ObsyncJson.Serialize(markers), cancellationToken);

    public async Task<int> GetRunRetentionDaysAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(RunRetentionDaysKey, cancellationToken).ConfigureAwait(false);
        return int.TryParse(value, out var days) && days >= 0 ? days : 0;
    }

    public Task SetRunRetentionDaysAsync(int days, CancellationToken cancellationToken = default) =>
        SetValueAsync(RunRetentionDaysKey, Math.Max(0, days).ToString(), cancellationToken);

    public async Task<CommitterIdentity> GetCommitterAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetValueAsync(CommitterKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? CommitterIdentity.Default : ObsyncJson.Deserialize<CommitterIdentity>(json);
    }

    public Task SetCommitterAsync(CommitterIdentity committer, CancellationToken cancellationToken = default) =>
        SetValueAsync(CommitterKey, ObsyncJson.Serialize(committer), cancellationToken);

    public Task<string?> GetWorkspacesRootOverrideAsync(CancellationToken cancellationToken = default) =>
        GetValueAsync(WorkspacesRootKey, cancellationToken);

    public Task SetWorkspacesRootOverrideAsync(string? path, CancellationToken cancellationToken = default) =>
        SetValueAsync(WorkspacesRootKey, string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim(), cancellationToken);

    public async Task<bool> GetNotifyRunFailuresAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(NotifyRunFailuresKey, cancellationToken).ConfigureAwait(false);
        return value is null || value != "0";
    }

    public Task SetNotifyRunFailuresAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetValueAsync(NotifyRunFailuresKey, enabled ? "1" : "0", cancellationToken);

    public async Task<DateTimeOffset?> GetLastFailureCheckAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(LastFailureCheckKey, cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }

    public Task SetLastFailureCheckAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
        SetValueAsync(LastFailureCheckKey, timestamp.ToString("O"), cancellationToken);

    private async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM app_settings WHERE key = $k;", new { k = key }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            new { k = key, v = value }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
