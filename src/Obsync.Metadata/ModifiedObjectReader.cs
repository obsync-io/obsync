using Microsoft.Data.SqlClient;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// Reads the incremental-scripting snapshot — every requested object with its
/// <c>sys.objects.modify_date</c> — in one bulk catalog query, following the
/// <see cref="DatabaseArtifactReader"/> style. The type-code mapping mirrors the scripting
/// queries exactly so the snapshot and the providers agree on which objects exist.
/// </summary>
public sealed class ModifiedObjectReader : IModifiedObjectReader
{
    private readonly ISqlConnectionStringFactory _connectionStrings;

    public ModifiedObjectReader(ISqlConnectionStringFactory connectionStrings) =>
        _connectionStrings = connectionStrings;

    // sys.objects type codes per scriptable type, matching MetadataScriptProvider's queries
    // (modules, DML triggers, synonyms, sequences) and the SMO table path (U).
    private static readonly IReadOnlyDictionary<SqlObjectType, string[]> TypeCodes = new Dictionary<SqlObjectType, string[]>
    {
        [SqlObjectType.Table] = ["U"],
        [SqlObjectType.View] = ["V"],
        [SqlObjectType.StoredProcedure] = ["P", "PC"],
        [SqlObjectType.Function] = ["FN", "IF", "TF", "FS", "FT"],
        // TA (CLR DML trigger) belongs here too: ReadDmlTriggersAsync has no type filter, so the
        // provider yields CLR triggers and a snapshot without them would not mirror it.
        [SqlObjectType.Trigger] = ["TR", "TA"],
        [SqlObjectType.Synonym] = ["SN"],
        [SqlObjectType.Sequence] = ["SO"],
    };

    /// <summary>
    /// Type codes whose definition lives in <c>sys.sql_modules</c>. Only these can be reported as
    /// definition-unavailable: every other type has no row there by design, and reading a missing
    /// row as "unscriptable" would exempt tables, synonyms and sequences from the violation rule
    /// that legitimately protects them.
    /// </summary>
    private static readonly HashSet<string> ModuleCodes =
        new(["V", "P", "PC", "FN", "IF", "TF", "FS", "FT", "TR", "TA"], StringComparer.Ordinal);

    public async Task<IReadOnlyList<ModifiedObjectSnapshotItem>> GetSnapshotAsync(
        SqlConnectionProfile profile, string? password, string database,
        IReadOnlyCollection<SqlObjectType> types, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, IReadOnlyCollection<string>? schemaFilter = null,
        CancellationToken cancellationToken = default)
    {
        var codeToType = new Dictionary<string, SqlObjectType>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!TypeCodes.TryGetValue(type, out var codes))
            {
                throw new ArgumentException($"{type} has no reliable sys.objects modify_date and cannot be snapshotted.", nameof(types));
            }

            foreach (var code in codes)
            {
                codeToType[code] = type;
            }
        }

        if (codeToType.Count == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        var orderedCodes = codeToType.Keys.Order(StringComparer.Ordinal).ToList();
        var placeholders = string.Join(", ", orderedCodes.Select((_, i) => $"@t{i}"));

        await using var command = connection.CreateCommand();
        // Sequences keep the ms-shipped rows because the sequence scripting query has no
        // is_ms_shipped filter — the snapshot must cover exactly what the providers can yield.
        // The schema filter matches the scripting queries' server-side narrowing: on a filtered
        // VLDB job the snapshot would otherwise stream every out-of-scope row on every run.
        var schemas = schemaFilter is { Count: > 0 } ? schemaFilter.ToList() : null;
        var schemaClause = schemas is null
            ? string.Empty
            : $" AND s.name IN ({string.Join(", ", schemas.Select((_, i) => $"@sf{i}"))})";
        // The LEFT JOIN answers one question only: will the server hand over a definition for this
        // module? A CLR module has no sys.sql_modules row; a WITH ENCRYPTION module has one with a
        // null definition. Both are projected to a bit server-side, so the nvarchar(max) definition
        // itself never crosses the wire -- this stays the cheap snapshot query it was.
        command.CommandText =
            $"""
             SELECT o.type, s.name, o.name, o.modify_date,
                    CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END
             FROM sys.objects o
             JOIN sys.schemas s ON s.schema_id = o.schema_id
             LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
             WHERE o.type IN ({placeholders}) AND (o.is_ms_shipped = 0 OR o.type = 'SO'){schemaClause};
             """;
        command.CommandTimeout = commandTimeoutSeconds;
        for (var i = 0; i < orderedCodes.Count; i++)
        {
            command.Parameters.AddWithValue($"@t{i}", orderedCodes[i]);
        }

        for (var i = 0; schemas is not null && i < schemas.Count; i++)
        {
            command.Parameters.AddWithValue($"@sf{i}", schemas[i]);
        }

        var items = new List<ModifiedObjectSnapshotItem>();

        // Schema names are pooled, exactly as the tracking projection pools them on the SQLite side.
        // A database of a million objects typically has a handful of schemas, and the reader hands
        // back a fresh string per row — so "dbo" was being materialised a million times, in a list
        // that is held alongside the equally large prior-state map.
        var schemaPool = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // sys.objects.type is char(2), so single-letter codes carry a trailing space.
            var code = reader.GetString(0).TrimEnd();
            var definitionUnavailable = ModuleCodes.Contains(code) && reader.GetInt32(4) == 1;
            var schema = reader.GetString(1);
            if (!schemaPool.TryGetValue(schema, out var pooled))
            {
                pooled = schema;
                schemaPool[schema] = pooled;
            }

            items.Add(new ModifiedObjectSnapshotItem(
                codeToType[code], pooled, reader.GetString(2), reader.GetDateTime(3),
                definitionUnavailable));
        }

        return items;
    }
}
