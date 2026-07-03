using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// The metadata fast path: scripts programmability objects from <c>sys.sql_modules</c> and builds
/// deterministic DDL for schemas, synonyms, and sequences directly from system catalog views.
/// A handful of bulk queries replace per-object round trips.
/// </summary>
public sealed class MetadataScriptProvider : IObjectScriptProvider
{
    private readonly ISqlConnectionStringFactory _connectionStrings;
    private readonly ILogger<MetadataScriptProvider> _logger;

    public MetadataScriptProvider(ISqlConnectionStringFactory connectionStrings, ILogger<MetadataScriptProvider> logger)
    {
        _connectionStrings = connectionStrings;
        _logger = logger;
    }

    public ScriptingStrategy Strategy => ScriptingStrategy.Metadata;

    public async IAsyncEnumerable<RawScriptedObject> ScriptAsync(
        ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionStrings.Create(request.Profile, request.Password, request.Database);
        await using var connection = new SqlConnection(connectionString);
        await SqlTransientErrors.RetryAsync(
            async ct =>
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                return true;
            },
            request.MaxRetries,
            cancellationToken).ConfigureAwait(false);

        await SqlSession.ApplyLockTimeoutAsync(connection, request.SqlLockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        foreach (var type in request.Types)
        {
            var stream = type switch
            {
                SqlObjectType.View => ReadModulesAsync(connection, type, ["V"], request, cancellationToken),
                SqlObjectType.StoredProcedure => ReadModulesAsync(connection, type, ["P", "PC"], request, cancellationToken),
                SqlObjectType.Function => ReadModulesAsync(connection, type, ["FN", "IF", "TF", "FS", "FT"], request, cancellationToken),
                SqlObjectType.Trigger => ReadDmlTriggersAsync(connection, request, cancellationToken),
                SqlObjectType.DatabaseDdlTrigger => ReadDatabaseDdlTriggersAsync(connection, request, cancellationToken),
                SqlObjectType.Schema => ReadSchemasAsync(connection, request, cancellationToken),
                SqlObjectType.Synonym => ReadSynonymsAsync(connection, request, cancellationToken),
                SqlObjectType.Sequence => ReadSequencesAsync(connection, request, cancellationToken),
                _ => Empty(),
            };

            await foreach (var item in stream.ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private static async IAsyncEnumerable<RawScriptedObject> Empty()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private async IAsyncEnumerable<RawScriptedObject> ReadModulesAsync(
        SqlConnection connection,
        SqlObjectType type,
        string[] typeCodes,
        ScriptRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var codes = string.Join(", ", typeCodes.Select((_, i) => $"@t{i}"));
        var sql = new StringBuilder(
            $"""
             SELECT s.name, o.name, o.object_id, m.definition
             FROM sys.sql_modules m
             JOIN sys.objects o ON o.object_id = m.object_id
             JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE o.is_ms_shipped = 0 AND o.type IN ({codes})
             """);
        AppendSchemaFilter(sql, "s.name", request.Selection.SchemaFilter);
        sql.Append(" ORDER BY s.name, o.name;");

        await using var command = CreateCommand(connection, sql.ToString(), request);
        for (var i = 0; i < typeCodes.Length; i++)
        {
            command.Parameters.AddWithValue($"@t{i}", typeCodes[i]);
        }

        AddSchemaFilterParameters(command, request.Selection.SchemaFilter);

        await using var reader = await ExecuteReaderWithRetryAsync(command, request.MaxRetries, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
            {
                // Encrypted (WITH ENCRYPTION) or CLR modules have no retrievable T-SQL definition.
                // Report them as skipped so the run warns instead of silently dropping objects.
                _logger.LogDebug("Definition unavailable for {Schema}.{Name}; reporting as skipped.",
                    reader.GetString(0), reader.GetString(1));
                yield return RawScriptedObject.Skipped(
                    new ScriptedObjectIdentity(type, reader.GetString(0), reader.GetString(1), reader.GetInt32(2)),
                    "The module definition is unavailable (encrypted with WITH ENCRYPTION, or a CLR object).");
                continue;
            }

            yield return new RawScriptedObject
            {
                Identity = new ScriptedObjectIdentity(type, reader.GetString(0), reader.GetString(1), reader.GetInt32(2)),
                Script = reader.GetString(3),
            };
        }
    }

    private async IAsyncEnumerable<RawScriptedObject> ReadDmlTriggersAsync(
        SqlConnection connection, ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            """
            SELECT ps.name, t.name, t.object_id, m.definition
            FROM sys.triggers t
            JOIN sys.objects po ON po.object_id = t.parent_id
            JOIN sys.schemas ps ON ps.schema_id = po.schema_id
            JOIN sys.sql_modules m ON m.object_id = t.object_id
            WHERE t.is_ms_shipped = 0 AND t.parent_class = 1
            """);
        AppendSchemaFilter(sql, "ps.name", request.Selection.SchemaFilter);
        sql.Append(" ORDER BY ps.name, t.name;");

        await using var command = CreateCommand(connection, sql.ToString(), request);
        AddSchemaFilterParameters(command, request.Selection.SchemaFilter);

        await using var reader = await ExecuteReaderWithRetryAsync(command, request.MaxRetries, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
            {
                yield return RawScriptedObject.Skipped(
                    new ScriptedObjectIdentity(SqlObjectType.Trigger, reader.GetString(0), reader.GetString(1), reader.GetInt32(2)),
                    "The trigger definition is unavailable (encrypted or CLR).");
                continue;
            }

            yield return new RawScriptedObject
            {
                Identity = new ScriptedObjectIdentity(SqlObjectType.Trigger, reader.GetString(0), reader.GetString(1), reader.GetInt32(2)),
                Script = reader.GetString(3),
            };
        }
    }

    private async IAsyncEnumerable<RawScriptedObject> ReadDatabaseDdlTriggersAsync(
        SqlConnection connection, ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT t.name, t.object_id, m.definition
            FROM sys.triggers t
            JOIN sys.sql_modules m ON m.object_id = t.object_id
            WHERE t.is_ms_shipped = 0 AND t.parent_class = 0
            ORDER BY t.name;
            """;

        await using var command = CreateCommand(connection, sql, request);
        await using var reader = await ExecuteReaderWithRetryAsync(command, request.MaxRetries, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
            {
                yield return RawScriptedObject.Skipped(
                    new ScriptedObjectIdentity(SqlObjectType.DatabaseDdlTrigger, string.Empty, reader.GetString(0), reader.GetInt32(1)),
                    "The DDL trigger definition is unavailable (encrypted or CLR).");
                continue;
            }

            yield return new RawScriptedObject
            {
                Identity = new ScriptedObjectIdentity(SqlObjectType.DatabaseDdlTrigger, string.Empty, reader.GetString(0), reader.GetInt32(1)),
                Script = reader.GetString(2),
            };
        }
    }

    private async IAsyncEnumerable<RawScriptedObject> ReadSchemasAsync(
        SqlConnection connection, ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            """
            SELECT s.name, dp.name
            FROM sys.schemas s
            JOIN sys.database_principals dp ON dp.principal_id = s.principal_id
            WHERE s.name NOT IN ('dbo','guest','INFORMATION_SCHEMA','sys','db_owner','db_accessadmin',
                'db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter',
                'db_denydatareader','db_denydatawriter')
            """);
        AppendSchemaFilter(sql, "s.name", request.Selection.SchemaFilter);
        sql.Append(" ORDER BY s.name;");

        await using var command = CreateCommand(connection, sql.ToString(), request);
        AddSchemaFilterParameters(command, request.Selection.SchemaFilter);

        await using var reader = await ExecuteReaderWithRetryAsync(command, request.MaxRetries, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var owner = reader.GetString(1);
            var script = $"CREATE SCHEMA {Quote(name)} AUTHORIZATION {Quote(owner)};";
            yield return new RawScriptedObject
            {
                Identity = new ScriptedObjectIdentity(SqlObjectType.Schema, string.Empty, name),
                Script = script,
            };
        }
    }

    private async IAsyncEnumerable<RawScriptedObject> ReadSynonymsAsync(
        SqlConnection connection, ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            """
            SELECT s.name, sy.name, sy.base_object_name
            FROM sys.synonyms sy
            JOIN sys.schemas s ON s.schema_id = sy.schema_id
            WHERE sy.is_ms_shipped = 0
            """);
        AppendSchemaFilter(sql, "s.name", request.Selection.SchemaFilter);
        sql.Append(" ORDER BY s.name, sy.name;");

        await using var command = CreateCommand(connection, sql.ToString(), request);
        AddSchemaFilterParameters(command, request.Selection.SchemaFilter);

        await using var reader = await ExecuteReaderWithRetryAsync(command, request.MaxRetries, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var baseObject = reader.GetString(2);
            var script = $"CREATE SYNONYM {Quote(schema)}.{Quote(name)} FOR {baseObject};";
            yield return new RawScriptedObject
            {
                Identity = new ScriptedObjectIdentity(SqlObjectType.Synonym, schema, name),
                Script = script,
            };
        }
    }

    private async IAsyncEnumerable<RawScriptedObject> ReadSequencesAsync(
        SqlConnection connection, ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            """
            SELECT s.name, q.name, TYPE_NAME(q.system_type_id), q.start_value, q.increment,
                   q.minimum_value, q.maximum_value, q.is_cycling, q.is_cached, q.cache_size
            FROM sys.sequences q
            JOIN sys.schemas s ON s.schema_id = q.schema_id
            """);
        AppendSchemaFilter(sql, "s.name", request.Selection.SchemaFilter, prefixWhere: true);
        sql.Append(" ORDER BY s.name, q.name;");

        await using var command = CreateCommand(connection, sql.ToString(), request);
        AddSchemaFilterParameters(command, request.Selection.SchemaFilter);

        await using var reader = await ExecuteReaderWithRetryAsync(command, request.MaxRetries, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var script = BuildSequenceScript(reader, schema, name);
            yield return new RawScriptedObject
            {
                Identity = new ScriptedObjectIdentity(SqlObjectType.Sequence, schema, name),
                Script = script,
            };
        }
    }

    private static string BuildSequenceScript(SqlDataReader reader, string schema, string name)
    {
        var dataType = reader.GetString(2);
        var startValue = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture);
        var increment = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture);
        var minValue = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture);
        var maxValue = Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture);
        var isCycling = reader.GetBoolean(7);
        var isCached = reader.GetBoolean(8);
        var cacheSize = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9);

        var builder = new StringBuilder();
        builder.Append("CREATE SEQUENCE ").Append(Quote(schema)).Append('.').Append(Quote(name)).Append('\n');
        builder.Append("    AS ").Append(Quote(dataType)).Append('\n');
        builder.Append("    START WITH ").Append(startValue).Append('\n');
        builder.Append("    INCREMENT BY ").Append(increment).Append('\n');
        builder.Append("    MINVALUE ").Append(minValue).Append('\n');
        builder.Append("    MAXVALUE ").Append(maxValue).Append('\n');
        builder.Append(isCycling ? "    CYCLE\n" : "    NO CYCLE\n");
        if (isCached)
        {
            builder.Append(cacheSize.HasValue ? $"    CACHE {cacheSize.Value};" : "    CACHE;");
        }
        else
        {
            builder.Append("    NO CACHE;");
        }

        return builder.ToString();
    }

    private static Task<SqlDataReader> ExecuteReaderWithRetryAsync(
        SqlCommand command, int maxRetries, CancellationToken cancellationToken) =>
        SqlTransientErrors.RetryAsync(ct => command.ExecuteReaderAsync(ct), maxRetries, cancellationToken);

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, ScriptRequest request)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = request.CommandTimeoutSeconds;
        return command;
    }

    private static void AppendSchemaFilter(StringBuilder sql, string column, ICollection<string> schemaFilter, bool prefixWhere = false)
    {
        if (schemaFilter.Count == 0)
        {
            return;
        }

        var placeholders = string.Join(", ", Enumerable.Range(0, schemaFilter.Count).Select(i => $"@sf{i}"));
        sql.Append(prefixWhere ? " WHERE " : " AND ").Append(column).Append(" IN (").Append(placeholders).Append(')');
    }

    private static void AddSchemaFilterParameters(SqlCommand command, ICollection<string> schemaFilter)
    {
        var index = 0;
        foreach (var schema in schemaFilter)
        {
            command.Parameters.AddWithValue($"@sf{index++}", schema);
        }
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
