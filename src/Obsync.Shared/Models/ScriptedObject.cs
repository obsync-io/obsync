using Obsync.Shared.Objects;

namespace Obsync.Shared.Models;

/// <summary>
/// Identifies a single SQL Server object within a database. Schema is empty for
/// object types that are not schema-scoped (logins, partition functions, etc.).
/// </summary>
public readonly record struct ScriptedObjectIdentity(
    SqlObjectType Type,
    string Schema,
    string Name,
    int? ObjectId = null)
{
    /// <summary>True when this object belongs to a schema.</summary>
    public bool IsSchemaScoped => !string.IsNullOrEmpty(Schema);

    /// <summary>A readable name such as <c>dbo.usp_GetCustomer</c> (or just the name when unscoped).</summary>
    public string QualifiedName => IsSchemaScoped ? $"{Schema}.{Name}" : Name;

    public override string ToString() => $"{Type}:{QualifiedName}";
}

/// <summary>
/// The result of scripting one object: its identity, the normalized script text, the
/// content hash used for change detection, and the deterministic repository-relative path.
/// </summary>
public sealed class ScriptedSqlObject
{
    public required ScriptedObjectIdentity Identity { get; init; }

    /// <summary>The normalized CREATE script for the object.</summary>
    public required string Script { get; init; }

    /// <summary>Lowercase hex SHA-256 of the normalized script.</summary>
    public required string Hash { get; init; }

    /// <summary>Repository-relative path using forward slashes, e.g. <c>procedures/dbo.usp_GetCustomer.sql</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Which engine path produced this script.</summary>
    public ScriptingStrategy ProducedBy { get; init; }
}

/// <summary>A single object change detected during a run.</summary>
public sealed class ObjectChange
{
    public required ChangeType ChangeType { get; init; }
    public required SqlObjectType ObjectType { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>Repository-relative path of the affected file.</summary>
    public required string RelativePath { get; init; }

    public string? PreviousHash { get; init; }
    public string? NewHash { get; init; }

    /// <summary>A readable name such as <c>dbo.usp_GetCustomer</c>.</summary>
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}
