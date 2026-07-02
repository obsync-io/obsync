using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Shared.Scripting;

/// <summary>A request to script a set of object types from one database.</summary>
public sealed class ScriptRequest
{
    public required SqlConnectionProfile Profile { get; init; }

    /// <summary>The SQL login password, when the profile uses SQL authentication; otherwise null.</summary>
    public string? Password { get; init; }

    public required string Database { get; init; }

    /// <summary>The object types to script — already filtered to this provider's strategy.</summary>
    public required IReadOnlyList<SqlObjectType> Types { get; init; }

    public required ObjectSelectionProfile Selection { get; init; }

    /// <summary>Command timeout to apply to SQL operations, in seconds.</summary>
    public int CommandTimeoutSeconds { get; init; } = 120;

    /// <summary>Number of attempts (1 = no retry) for transient SQL failures while reading metadata.</summary>
    public int MaxRetries { get; init; } = 3;
}

/// <summary>The raw (pre-normalization) script for a single object, as produced by a provider.</summary>
public sealed class RawScriptedObject
{
    public required ScriptedObjectIdentity Identity { get; init; }

    /// <summary>The CREATE script for the object, before normalization and hashing.</summary>
    public required string Script { get; init; }

    /// <summary>
    /// When set, this object could NOT be scripted (e.g. an encrypted or CLR module whose
    /// definition is unavailable, or an object SMO failed to script). The engine records it as a
    /// skipped/failed object and surfaces a warning instead of silently dropping it. <see cref="Script"/>
    /// is ignored when this is non-null.
    /// </summary>
    public string? SkipReason { get; init; }

    /// <summary>Creates a normal, successfully-scripted object.</summary>
    public static RawScriptedObject Scripted(ScriptedObjectIdentity identity, string script) =>
        new() { Identity = identity, Script = script };

    /// <summary>Creates a marker for an object that could not be scripted, so it is reported, not dropped.</summary>
    public static RawScriptedObject Skipped(ScriptedObjectIdentity identity, string reason) =>
        new() { Identity = identity, Script = string.Empty, SkipReason = reason };
}

/// <summary>
/// Produces scripts for the object types it owns. The engine routes each type to the provider
/// whose <see cref="Strategy"/> matches the type's catalog descriptor, enabling the hybrid
/// metadata + SMO engine.
/// </summary>
public interface IObjectScriptProvider
{
    /// <summary>The scripting strategy this provider implements.</summary>
    ScriptingStrategy Strategy { get; }

    /// <summary>Streams scripted objects for the requested types. Honors cancellation.</summary>
    IAsyncEnumerable<RawScriptedObject> ScriptAsync(ScriptRequest request, CancellationToken cancellationToken = default);
}
