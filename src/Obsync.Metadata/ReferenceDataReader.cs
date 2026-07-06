using Microsoft.Data.SqlClient;
using Obsync.Shared.Models;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// Reads one table's rows and renders them through <see cref="ReferenceDataScriptBuilder"/>.
/// Missing tables, tables over the row cap, and tables with no deterministic ordering are
/// returned as skips (reported by the engine), never as run failures — one bad entry in the
/// reference list must not sink the whole sync.
/// </summary>
public sealed class ReferenceDataReader : IReferenceDataReader
{
    private readonly ISqlConnectionStringFactory _connectionStrings;

    public ReferenceDataReader(ISqlConnectionStringFactory connectionStrings) =>
        _connectionStrings = connectionStrings;

    // Column types that cannot appear in an ORDER BY, so they can't anchor deterministic output.
    private static readonly HashSet<string> UnsortableTypes =
        new(StringComparer.OrdinalIgnoreCase) { "xml", "geography", "geometry", "text", "ntext", "image" };

    private const string ColumnsQuery =
        """
        SELECT c.name, t.name AS type_name, c.is_identity, c.is_computed
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@qualified)
        ORDER BY c.column_id;
        """;

    private const string PrimaryKeyQuery =
        """
        SELECT c.name
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(@qualified) AND i.is_primary_key = 1 AND ic.is_included_column = 0
        ORDER BY ic.key_ordinal;
        """;

    public async Task<ReferenceDataResult> ReadTableDataAsync(
        SqlConnectionProfile profile, string? password, string database, string schema, string table,
        int maxRows, int commandTimeoutSeconds, int lockTimeoutSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var qualified = $"{ReferenceDataScriptBuilder.Quote(schema)}.{ReferenceDataScriptBuilder.Quote(table)}";

        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        var allColumns = new List<(string Name, string TypeName, bool IsIdentity, bool IsComputed)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = ColumnsQuery;
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.AddWithValue("@qualified", qualified);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                allColumns.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
            }
        }

        if (allColumns.Count == 0)
        {
            return ReferenceDataResult.Skipped($"Table {schema}.{table} was not found in {database}.");
        }

        // Computed and rowversion columns are not insertable and would make output nondeterministic.
        var columns = allColumns
            .Where(c => !c.IsComputed && !c.TypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                        && !c.TypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase))
            .Select(c => new ReferenceDataColumn(c.Name, c.TypeName, c.IsIdentity))
            .ToList();
        if (columns.Count == 0)
        {
            return ReferenceDataResult.Skipped($"Table {schema}.{table} has no insertable columns.");
        }

        var orderColumns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = PrimaryKeyQuery;
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.AddWithValue("@qualified", qualified);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                orderColumns.Add(reader.GetString(0));
            }
        }

        if (orderColumns.Count == 0)
        {
            // No primary key: order by every sortable scripted column for a stable file.
            orderColumns = [.. columns.Where(c => !UnsortableTypes.Contains(c.DataTypeName)).Select(c => c.Name)];
            if (orderColumns.Count == 0)
            {
                return ReferenceDataResult.Skipped(
                    $"Table {schema}.{table} has no primary key and no sortable columns, so its data cannot be scripted deterministically.");
            }
        }

        long rowCount;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT_BIG(*) FROM {qualified};";
            command.CommandTimeout = commandTimeoutSeconds;
            rowCount = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (rowCount > maxRows)
        {
            return ReferenceDataResult.Skipped(
                $"Table {schema}.{table} has {rowCount:N0} rows — over the {maxRows:N0}-row reference-data cap. " +
                "Raise the cap in the job's advanced settings, or remove the table from the reference list.");
        }

        var rows = new List<object?[]>();
        await using (var command = connection.CreateCommand())
        {
            var columnList = string.Join(", ", columns.Select(c => ReferenceDataScriptBuilder.Quote(c.Name)));
            var orderList = string.Join(", ", orderColumns.Select(ReferenceDataScriptBuilder.Quote));
            command.CommandText = $"SELECT {columnList} FROM {qualified} ORDER BY {orderList};";
            command.CommandTimeout = commandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(values);
            }
        }

        var script = ReferenceDataScriptBuilder.Build(schema, table, columns, rows, orderColumns);
        return ReferenceDataResult.Scripted(script, rows.Count);
    }
}
