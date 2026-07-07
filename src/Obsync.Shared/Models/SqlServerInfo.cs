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

/// <summary>One related object in a dependency lookup (a dependent or a referenced entity).</summary>
public sealed class SqlDependencyItem
{
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Friendly kind, e.g. "View", "Stored procedure", "Table (foreign key)".</summary>
    public string TypeLabel { get; init; } = string.Empty;

    /// <summary>False for cross-database or unresolved references, which cannot be drilled into.</summary>
    public bool IsDrillable { get; init; } = true;

    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>Both directions of an object's dependency graph, one level deep.</summary>
public sealed class SqlObjectDependencies
{
    /// <summary>Objects affected by a change to this one: referencing modules, foreign-key tables, triggers.</summary>
    public IReadOnlyList<SqlDependencyItem> UsedBy { get; init; } = [];

    /// <summary>Entities this object's definition references.</summary>
    public IReadOnlyList<SqlDependencyItem> Uses { get; init; } = [];
}
