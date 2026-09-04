using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Obsync.Shared.Models;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// Counts the objects Obsync does not script, so that their absence from the repository is reported
/// instead of silent.
///
/// Each type is probed by its own small query rather than one <c>UNION ALL</c>, deliberately. The
/// catalog views differ by SQL Server version — database scoped credentials and external data
/// sources arrived in 2016, audit specifications and stoplists in 2008 — and this product documents
/// no minimum version. A single statement referencing a view that does not exist fails as a whole
/// and would report nothing at all; a probe that fails on its own is simply left out.
///
/// Nothing here can fail a run. Coverage reporting is a diagnostic, and a diagnostic that can break
/// the sync it describes is worse than the silence it replaces.
/// </summary>
public sealed class UnsupportedObjectReader : IUnsupportedObjectReader
{
    /// <summary>
    /// (label, count query). Types Obsync scripts are excluded — this is only what it cannot.
    /// <c>sys.objects</c> type codes: SQ = Service Broker queue, R = rule, D = bound default.
    /// External tables are ordinary <c>U</c> rows distinguished by <c>is_external</c>.
    /// </summary>
    private static readonly (string Label, string Query)[] Probes =
    [
        ("Service Broker queue", "SELECT COUNT(*) FROM sys.objects WHERE type = 'SQ' AND is_ms_shipped = 0"),
        ("Service Broker service", "SELECT COUNT(*) FROM sys.services WHERE service_id > 65535"),
        ("Service Broker contract", "SELECT COUNT(*) FROM sys.service_contracts WHERE service_contract_id > 65535"),
        ("Service Broker message type", "SELECT COUNT(*) FROM sys.service_message_types WHERE message_type_id > 65535"),
        ("Service Broker route", "SELECT COUNT(*) FROM sys.routes WHERE name <> 'AutoCreatedLocal'"),
        ("Rule", "SELECT COUNT(*) FROM sys.objects WHERE type = 'R' AND is_ms_shipped = 0"),
        ("Bound default", "SELECT COUNT(*) FROM sys.objects WHERE type = 'D' AND parent_object_id = 0 AND is_ms_shipped = 0"),
        ("External table", "SELECT COUNT(*) FROM sys.tables WHERE is_external = 1"),
        ("External data source", "SELECT COUNT(*) FROM sys.external_data_sources"),
        ("Certificate", "SELECT COUNT(*) FROM sys.certificates WHERE pvt_key_last_backup_date IS NOT NULL OR name NOT LIKE '##%'"),
        ("Asymmetric key", "SELECT COUNT(*) FROM sys.asymmetric_keys WHERE name NOT LIKE '##%'"),
        ("Symmetric key", "SELECT COUNT(*) FROM sys.symmetric_keys WHERE name NOT LIKE '##%'"),
        ("Database scoped credential", "SELECT COUNT(*) FROM sys.database_scoped_credentials"),
        ("Plan guide", "SELECT COUNT(*) FROM sys.plan_guides"),
        ("Full-text stoplist", "SELECT COUNT(*) FROM sys.fulltext_stoplists"),
        ("Database audit specification", "SELECT COUNT(*) FROM sys.database_audit_specifications"),
        ("Event notification", "SELECT COUNT(*) FROM sys.event_notifications"),
    ];

    private readonly ISqlConnectionStringFactory _connectionStrings;
    private readonly ILogger<UnsupportedObjectReader> _logger;

    public UnsupportedObjectReader(
        ISqlConnectionStringFactory connectionStrings, ILogger<UnsupportedObjectReader> logger)
    {
        _connectionStrings = connectionStrings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UnsupportedObjectGroup>> ReadAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        var groups = new List<UnsupportedObjectGroup>();
        try
        {
            await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

            foreach (var (label, query) in Probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await CountAsync(connection, query, commandTimeoutSeconds, label, cancellationToken)
                    .ConfigureAwait(false);
                if (count > 0)
                {
                    groups.Add(new UnsupportedObjectGroup(label, count));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Connecting failed, or the login cannot read the catalog. Report nothing rather than
            // anything: the run itself has its own, better error for a connection that is broken.
            _logger.LogDebug("Unsupported-object census for {Database} could not run: {Message}", database, ex.Message);
            return [];
        }

        return groups;
    }

    private async Task<int> CountAsync(
        SqlConnection connection, string query, int commandTimeoutSeconds, string label, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = commandTimeoutSeconds;
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is int count ? count : 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The view does not exist on this version, or the login cannot see it. Either way this
            // type simply goes uncounted — one probe's absence must not cost the whole census.
            _logger.LogDebug("Unsupported-object probe for {Label} skipped: {Message}", label, ex.Message);
            return 0;
        }
    }
}
