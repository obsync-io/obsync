using System.Runtime.InteropServices;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;

namespace Obsync.App.Services;

/// <summary>One "key: value" line of the About tab's support information block.</summary>
public sealed record SupportInfoRow(string Key, string Value);

/// <summary>
/// Assembles the Settings → About support information: component versions and environment facts a
/// support thread always asks for first. Contains no credential material by construction — every row
/// is a version, an OS fact, or a schema identifier (guarded by a key-whitelist regression test).
/// </summary>
public interface ISupportInfoService
{
    Task<IReadOnlyList<SupportInfoRow>> GetAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISupportInfoService" />
public sealed class SupportInfoService : ISupportInfoService
{
    private readonly IAppSettingsRepository _settings;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IClock _clock;

    public SupportInfoService(
        IAppSettingsRepository settings,
        IDbConnectionFactory connectionFactory,
        IDiagnosticsService diagnostics,
        IClock clock)
    {
        _settings = settings;
        _connectionFactory = connectionFactory;
        _diagnostics = diagnostics;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SupportInfoRow>> GetAsync(CancellationToken cancellationToken = default) =>
    [
        new("Obsync", VersionInfo.Of(typeof(App).Assembly)),
        new("Engine", VersionInfo.Of(typeof(Engine.ISyncEngine).Assembly)),
        new("Windows Service", await GetServiceVersionAsync(cancellationToken).ConfigureAwait(false)),
        new(".NET runtime", Environment.Version.ToString()),
        new("Git", await GetGitVersionAsync(cancellationToken).ConfigureAwait(false)),
        new("Windows", RuntimeInformation.OSDescription),
        new("State database schema", await GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false)),
    ];

    /// <summary>The service's version from its heartbeat — only when fresh, because a stale
    /// heartbeat means the version on disk may have changed since the service last ran.</summary>
    private async Task<string> GetServiceVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var heartbeat = await _settings.GetSchedulerHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            var fresh = heartbeat is not null
                && _clock.UtcNow - heartbeat.TimestampUtc <= SchedulerHealthService.HeartbeatFreshness;
            return fresh ? heartbeat!.Version : "not running";
        }
        catch (Exception)
        {
            return "not running";
        }
    }

    // Reuses the Git CLI diagnostic's probe — one resolution path, one answer.
    private async Task<string> GetGitVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _diagnostics.GetGitVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The highest applied migration from the <c>__migrations</c> table the
    /// <see cref="DatabaseInitializer"/> maintains. Version ids are zero-padded (V001…), so the
    /// ordinal MAX is the latest; only the "V###" prefix is shown.
    /// </summary>
    private async Task<string> GetSchemaVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM __migrations;";
            var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (string.IsNullOrEmpty(version))
            {
                return "unknown";
            }

            var separator = version.IndexOf("__", StringComparison.Ordinal);
            return separator > 0 ? version[..separator] : version;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
