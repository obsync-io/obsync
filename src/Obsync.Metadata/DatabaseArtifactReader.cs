using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Obsync.Shared.Models;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// Reads database-level options and permissions — and the server-level configuration — directly
/// from catalog views. Each artifact is a single bulk query (no N+1) and produces deterministic,
/// timestamp-free output: the option script captures settings only (no environment-specific file
/// paths), the permission script is ordered by its own statement text, and the server
/// configuration is ordered by option name for stable diffs.
/// </summary>
public sealed class DatabaseArtifactReader : IDatabaseArtifactReader
{
    private readonly ISqlConnectionStringFactory _connectionStrings;

    public DatabaseArtifactReader(ISqlConnectionStringFactory connectionStrings) =>
        _connectionStrings = connectionStrings;

    private const string OptionsQuery =
        """
        SELECT compatibility_level, collation_name, recovery_model_desc, page_verify_option_desc,
               is_ansi_null_default_on, is_ansi_nulls_on, is_ansi_padding_on, is_ansi_warnings_on,
               is_arithabort_on, is_concat_null_yields_null_on, is_numeric_roundabort_on, is_quoted_identifier_on,
               is_recursive_triggers_on, is_cursor_close_on_commit_on, is_local_cursor_default,
               is_auto_close_on, is_auto_shrink_on, is_auto_create_stats_on, is_auto_update_stats_on,
               is_auto_update_stats_async_on, snapshot_isolation_state, is_read_committed_snapshot_on,
               is_date_correlation_on, is_parameterization_forced, delayed_durability_desc,
               is_trustworthy_on, is_db_chaining_on
        FROM sys.databases
        WHERE database_id = DB_ID();
        """;

    // Builds each grant statement in T-SQL (so name resolution stays in the engine) and orders by
    // the statement text, giving a deterministic file. Covers DATABASE, OBJECT/COLUMN, SCHEMA and
    // TYPE scoped permissions — the classes that make up the vast majority of real-world grants.
    private const string PermissionsQuery =
        """
        SELECT
            CASE dp.state WHEN 'W' THEN 'GRANT' ELSE dp.state_desc END COLLATE DATABASE_DEFAULT
            + ' ' + dp.permission_name COLLATE DATABASE_DEFAULT
            + CASE dp.class
                WHEN 0 THEN ''
                WHEN 1 THEN ' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(dp.major_id)) + '.' + QUOTENAME(OBJECT_NAME(dp.major_id))
                           + CASE WHEN dp.minor_id > 0 THEN ' (' + QUOTENAME(col.name) + ')' ELSE '' END
                WHEN 3 THEN ' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(dp.major_id))
                WHEN 6 THEN ' ON TYPE::' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)
              END
            + ' TO ' + QUOTENAME(grantee.name)
            + CASE WHEN dp.state = 'W' THEN ' WITH GRANT OPTION' ELSE '' END
            + ';' AS stmt
        FROM sys.database_permissions dp
        JOIN sys.database_principals grantee ON grantee.principal_id = dp.grantee_principal_id
        LEFT JOIN sys.columns col ON dp.class = 1 AND col.object_id = dp.major_id AND col.column_id = dp.minor_id
        LEFT JOIN sys.types t ON dp.class = 6 AND t.user_type_id = dp.major_id
        WHERE dp.class IN (0, 1, 3, 6)
          AND (dp.class <> 1 OR OBJECT_NAME(dp.major_id) IS NOT NULL)
        ORDER BY stmt;
        """;

    public async Task<string> ReadDatabaseOptionsAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = OptionsQuery;
        command.CommandTimeout = commandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return $"/* Database '{database}' was not found in sys.databases. */\n";
        }

        return BuildOptionsScript(reader, database);
    }

    // The configured (not run) value is what a DBA declares; ORDER BY name keeps the file stable.
    private const string ServerConfigurationQuery =
        """
        SELECT name, CAST(value AS int) AS configured
        FROM sys.configurations
        ORDER BY name;
        """;

    public async Task<string> ReadPermissionsAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = PermissionsQuery;
        command.CommandTimeout = commandTimeoutSeconds;

        var statements = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                statements.Add(reader.GetString(0));
            }
        }

        var builder = new StringBuilder();
        builder.Append("/* Database-scoped permissions for ").Append(database).Append(". Generated by Obsync. */\n");
        if (statements.Count == 0)
        {
            builder.Append("-- (no explicit database-scoped permissions)\n");
            return builder.ToString();
        }

        foreach (var statement in statements)
        {
            builder.Append(statement).Append('\n');
        }

        return builder.ToString();
    }

    public async Task<string> ReadServerConfigurationAsync(
        SqlConnectionProfile profile, string? password, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        // Instance scope: connect with no catalog.
        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = ServerConfigurationQuery;
        command.CommandTimeout = commandTimeoutSeconds;

        var builder = new StringBuilder();
        builder.Append("/* Server-level sp_configure values for ").Append(profile.ServerName).Append(". Generated by Obsync. */\n");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0).Replace("'", "''", StringComparison.Ordinal);
            var configured = reader.GetInt32(1);
            builder.Append("EXEC sp_configure N'").Append(name).Append("', ")
                .Append(configured.ToString(CultureInfo.InvariantCulture)).Append(";\n");
        }

        return builder.ToString();
    }

    private static string BuildOptionsScript(SqlDataReader reader, string database)
    {
        var compatibilityLevel = reader.GetByte(0);
        var collation = reader.IsDBNull(1) ? null : reader.GetString(1);
        var recoveryModel = reader.GetString(2);
        var pageVerify = reader.IsDBNull(3) ? null : reader.GetString(3);
        var ansiNullDefault = reader.GetBoolean(4);
        var ansiNulls = reader.GetBoolean(5);
        var ansiPadding = reader.GetBoolean(6);
        var ansiWarnings = reader.GetBoolean(7);
        var arithabort = reader.GetBoolean(8);
        var concatNullYieldsNull = reader.GetBoolean(9);
        var numericRoundabort = reader.GetBoolean(10);
        var quotedIdentifier = reader.GetBoolean(11);
        var recursiveTriggers = reader.GetBoolean(12);
        var cursorCloseOnCommit = reader.GetBoolean(13);
        var localCursorDefault = reader.GetBoolean(14);
        var autoClose = reader.GetBoolean(15);
        var autoShrink = reader.GetBoolean(16);
        var autoCreateStats = reader.GetBoolean(17);
        var autoUpdateStats = reader.GetBoolean(18);
        var autoUpdateStatsAsync = reader.GetBoolean(19);
        var snapshotIsolationState = reader.GetByte(20);
        var readCommittedSnapshot = reader.GetBoolean(21);
        var dateCorrelation = reader.GetBoolean(22);
        var parameterizationForced = reader.GetBoolean(23);
        var delayedDurability = reader.IsDBNull(24) ? "DISABLED" : reader.GetString(24);
        var trustworthy = reader.GetBoolean(25);
        var dbChaining = reader.GetBoolean(26);

        var db = Quote(database);
        var builder = new StringBuilder();
        builder.Append("/* Database-level options for ").Append(database).Append(". Generated by Obsync. */\n");

        void Set(string clause) =>
            builder.Append("ALTER DATABASE ").Append(db).Append(" SET ").Append(clause).Append(";\n");

        builder.Append("ALTER DATABASE ").Append(db).Append(" SET COMPATIBILITY_LEVEL = ")
            .Append(compatibilityLevel.ToString(CultureInfo.InvariantCulture)).Append(";\n");
        if (collation is not null)
        {
            builder.Append("ALTER DATABASE ").Append(db).Append(" COLLATE ").Append(collation).Append(";\n");
        }

        Set($"RECOVERY {recoveryModel}");
        if (pageVerify is not null)
        {
            Set($"PAGE_VERIFY {pageVerify}");
        }

        Set($"ANSI_NULL_DEFAULT {OnOff(ansiNullDefault)}");
        Set($"ANSI_NULLS {OnOff(ansiNulls)}");
        Set($"ANSI_PADDING {OnOff(ansiPadding)}");
        Set($"ANSI_WARNINGS {OnOff(ansiWarnings)}");
        Set($"ARITHABORT {OnOff(arithabort)}");
        Set($"CONCAT_NULL_YIELDS_NULL {OnOff(concatNullYieldsNull)}");
        Set($"NUMERIC_ROUNDABORT {OnOff(numericRoundabort)}");
        Set($"QUOTED_IDENTIFIER {OnOff(quotedIdentifier)}");
        Set($"RECURSIVE_TRIGGERS {OnOff(recursiveTriggers)}");
        Set($"CURSOR_CLOSE_ON_COMMIT {OnOff(cursorCloseOnCommit)}");
        Set($"CURSOR_DEFAULT {(localCursorDefault ? "LOCAL" : "GLOBAL")}");
        Set($"AUTO_CLOSE {OnOff(autoClose)}");
        Set($"AUTO_SHRINK {OnOff(autoShrink)}");
        Set($"AUTO_CREATE_STATISTICS {OnOff(autoCreateStats)}");
        Set($"AUTO_UPDATE_STATISTICS {OnOff(autoUpdateStats)}");
        Set($"AUTO_UPDATE_STATISTICS_ASYNC {OnOff(autoUpdateStatsAsync)}");
        Set($"ALLOW_SNAPSHOT_ISOLATION {(snapshotIsolationState == 1 ? "ON" : "OFF")}");
        Set($"READ_COMMITTED_SNAPSHOT {OnOff(readCommittedSnapshot)}");
        Set($"DATE_CORRELATION_OPTIMIZATION {OnOff(dateCorrelation)}");
        Set($"PARAMETERIZATION {(parameterizationForced ? "FORCED" : "SIMPLE")}");
        Set($"DELAYED_DURABILITY = {delayedDurability}");
        Set($"TRUSTWORTHY {OnOff(trustworthy)}");
        Set($"DB_CHAINING {OnOff(dbChaining)}");

        return builder.ToString();
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
