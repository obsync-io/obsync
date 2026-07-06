using System.Globalization;
using System.Text;

namespace Obsync.Metadata;

/// <summary>A column participating in a reference-data script.</summary>
public sealed record ReferenceDataColumn(string Name, string DataTypeName, bool IsIdentity);

/// <summary>
/// Renders rows into a deterministic T-SQL INSERT script: fixed column order, PK-ordered rows,
/// invariant-culture literals, and batched multi-row VALUES. Pure — no SQL connectivity — so the
/// exact output is unit-testable.
/// </summary>
public static class ReferenceDataScriptBuilder
{
    /// <summary>Rows per INSERT statement (SQL Server allows at most 1000 in a VALUES list).</summary>
    private const int RowsPerInsert = 1000;

    public static string Build(
        string schema, string table, IReadOnlyList<ReferenceDataColumn> columns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<string> orderedBy)
    {
        var qualified = $"{Quote(schema)}.{Quote(table)}";
        var hasIdentity = columns.Any(c => c.IsIdentity);

        var builder = new StringBuilder();
        builder.Append("-- Reference data for ").Append(qualified)
            .Append(" — ").Append(rows.Count.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" row(s).");
        builder.Append("-- Ordered by ").Append(string.Join(", ", orderedBy)).AppendLine(" for stable diffs.");
        builder.AppendLine("SET NOCOUNT ON;");

        if (rows.Count == 0)
        {
            builder.AppendLine().AppendLine("-- (no rows)");
            return builder.ToString();
        }

        if (hasIdentity)
        {
            builder.AppendLine().Append("SET IDENTITY_INSERT ").Append(qualified).AppendLine(" ON;");
        }

        var columnList = string.Join(", ", columns.Select(c => Quote(c.Name)));
        for (var start = 0; start < rows.Count; start += RowsPerInsert)
        {
            builder.AppendLine()
                .Append("INSERT INTO ").Append(qualified).Append(" (").Append(columnList).AppendLine(")")
                .AppendLine("VALUES");

            var batchEnd = Math.Min(start + RowsPerInsert, rows.Count);
            for (var i = start; i < batchEnd; i++)
            {
                builder.Append("    (").Append(string.Join(", ", rows[i].Select(FormatLiteral)))
                    .AppendLine(i == batchEnd - 1 ? ");" : "),");
            }
        }

        if (hasIdentity)
        {
            builder.AppendLine().Append("SET IDENTITY_INSERT ").Append(qualified).AppendLine(" OFF;");
        }

        return builder.ToString();
    }

    internal static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

    internal static string FormatLiteral(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => $"N'{s.Replace("'", "''")}'",
        char c => $"N'{(c == '\'' ? "''" : c.ToString())}'",
        bool b => b ? "1" : "0",
        byte[] bytes => bytes.Length == 0 ? "0x" : $"0x{Convert.ToHexString(bytes)}",
        Guid g => $"'{g:D}'",
        DateTime dt => $"'{dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'",
        DateTimeOffset dto => $"'{dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)}'",
        TimeSpan ts => $"'{ts.ToString("c", CultureInfo.InvariantCulture)}'",
        DateOnly d => $"'{d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
        TimeOnly t => $"'{t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'",
        float f => f.ToString("G9", CultureInfo.InvariantCulture),
        double d => d.ToString("G17", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        // Unknown provider-specific types (sql_variant payloads etc.): script as a text literal —
        // deterministic, and valid for the common variant cases.
        _ => $"N'{(value.ToString() ?? string.Empty).Replace("'", "''")}'",
    };
}
