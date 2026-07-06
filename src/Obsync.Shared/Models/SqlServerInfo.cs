namespace Obsync.Shared.Models;

/// <summary>Basic facts about a connected SQL Server instance, shown after a successful test.</summary>
public sealed class SqlServerInfo
{
    public string ProductVersion { get; init; } = string.Empty;
    public string Edition { get; init; } = string.Empty;
    public string ProductLevel { get; init; } = string.Empty;

    /// <summary>Major version number (e.g. 16 for SQL Server 2022).</summary>
    public int MajorVersion { get; init; }
}

/// <summary>A user database available to script on a connected instance.</summary>
public sealed class SqlDatabaseInfo
{
    public string Name { get; init; } = string.Empty;
    public long SizeMb { get; init; }
    public bool IsOnline { get; init; } = true;
}

/// <summary>A table in a database, listed when picking reference-data tables.</summary>
public sealed class SqlTableInfo
{
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long RowCount { get; init; }

    /// <summary>The <c>schema.table</c> form used in the job's reference-table list.</summary>
    public string QualifiedName => $"{Schema}.{Name}";
}
