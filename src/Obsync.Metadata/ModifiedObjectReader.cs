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
        [SqlObjectType.Trigger] = ["TR"],
        [SqlObjectType.Synonym] = ["SN"],
        [SqlObjectType.Sequence] = ["SO"],
    };

    public async Task<IReadOnlyList<ModifiedObjectSnapshotItem>> GetSnapshotAsync(
        SqlConnectionProfile profile, string? password, string database,
        IReadOnlyCollection<SqlObjectType> types, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
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
        command.CommandText =
            $"""
             SELECT o.type, s.name, o.name, o.modify_date
             FROM sys.objects o
             JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE o.type IN ({placeholders}) AND (o.is_ms_shipped = 0 OR o.type = 'SO');
             """;
        command.CommandTimeout = commandTimeoutSeconds;
        for (var i = 0; i < orderedCodes.Count; i++)
        {
            command.Parameters.AddWithValue($"@t{i}", orderedCodes[i]);
        }

        var items = new List<ModifiedObjectSnapshotItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // sys.objects.type is char(2), so single-letter codes carry a trailing space.
            var code = reader.GetString(0).TrimEnd();
            items.Add(new ModifiedObjectSnapshotItem(
                codeToType[code], reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        }

        return items;
    }
}
