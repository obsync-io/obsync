using Microsoft.Data.SqlClient;
using Obsync.Shared.Models;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// Reads the data-dictionary facts for the docs artifact in one bulk round trip: every column of the
/// first N tables (alphabetical) with type, nullability, default, primary-key membership, and
/// MS_Description extended properties, plus the total table count for the truncation note.
/// </summary>
public sealed class DatabaseDocumentationReader : IDatabaseDocumentationReader
{
    private readonly ISqlConnectionStringFactory _connectionStrings;

    public DatabaseDocumentationReader(ISqlConnectionStringFactory connectionStrings) =>
        _connectionStrings = connectionStrings;

    private const string Query =
        """
        SELECT s.name AS SchemaName, t.name AS TableName,
               CAST(ep.value AS nvarchar(4000)) AS TableDescription,
               c.name AS ColumnName, ty.name AS TypeName, c.max_length, c.precision, c.scale,
               c.is_nullable, c.is_identity, c.is_computed,
               dc.definition AS DefaultDefinition,
               CAST(epc.value AS nvarchar(4000)) AS ColumnDescription,
               CASE WHEN pkc.column_id IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsPrimaryKey
        FROM (
            SELECT TOP (@cap) t0.object_id, t0.name, t0.schema_id
            FROM sys.tables t0
            WHERE t0.is_ms_shipped = 0
            ORDER BY SCHEMA_NAME(t0.schema_id), t0.name
        ) t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns c ON c.object_id = t.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints dc
               ON dc.parent_object_id = t.object_id AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.extended_properties ep
               ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
        LEFT JOIN sys.extended_properties epc
               ON epc.class = 1 AND epc.major_id = t.object_id AND epc.minor_id = c.column_id AND epc.name = 'MS_Description'
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            WHERE i.is_primary_key = 1
        ) pkc ON pkc.object_id = t.object_id AND pkc.column_id = c.column_id
        ORDER BY s.name, t.name, c.column_id;

        SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;
        """;

    public async Task<DatabaseDocumentationData> ReadAsync(
        SqlConnectionProfile profile, string? password, string database, int maxTables, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Query;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@cap", maxTables);

        var tables = new List<DocumentationTable>();
        string? currentSchema = null, currentName = null, currentDescription = null;
        var columns = new List<DocumentationColumn>();

        void FlushTable()
        {
            if (currentName is not null)
            {
                tables.Add(new DocumentationTable(currentSchema!, currentName, currentDescription, [.. columns]));
                columns.Clear();
            }
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            if (!string.Equals(schema, currentSchema, StringComparison.Ordinal)
                || !string.Equals(table, currentName, StringComparison.Ordinal))
            {
                FlushTable();
                currentSchema = schema;
                currentName = table;
                currentDescription = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            columns.Add(new DocumentationColumn(
                Name: reader.GetString(3),
                DataType: FormatDataType(reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7)),
                IsNullable: reader.GetBoolean(8),
                IsIdentity: reader.GetBoolean(9),
                IsComputed: reader.GetBoolean(10),
                Default: reader.IsDBNull(11) ? null : reader.GetString(11),
                Description: reader.IsDBNull(12) ? null : reader.GetString(12),
                IsPrimaryKey: reader.GetBoolean(13)));
        }

        FlushTable();

        var total = tables.Count;
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
            && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            total = reader.GetInt32(0);
        }

        return new DatabaseDocumentationData(tables, total);
    }

    /// <summary>SSMS-style type display: <c>nvarchar(50)</c>, <c>decimal(18,2)</c>, <c>varbinary(max)</c>…</summary>
    internal static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
    {
        switch (typeName)
        {
            case "nvarchar" or "nchar":
                return maxLength < 0 ? $"{typeName}(max)" : $"{typeName}({maxLength / 2})";
            case "varchar" or "char" or "varbinary" or "binary":
                return maxLength < 0 ? $"{typeName}(max)" : $"{typeName}({maxLength})";
            case "decimal" or "numeric":
                return $"{typeName}({precision},{scale})";
            case "datetime2" or "datetimeoffset" or "time":
                return $"{typeName}({scale})";
            default:
                return typeName;
        }
    }
}
