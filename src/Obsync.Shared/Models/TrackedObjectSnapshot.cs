using Obsync.Shared.Objects;

namespace Obsync.Shared.Models;

/// <summary>
/// What a run needs to know about an object it tracked last time: enough to tell whether the object
/// changed, where its file went, and how to remove it if it is gone.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="TrackedObjectState"/>. That type is the full persisted row — job and
/// database identity, the object id, three timestamps, the commit sha, the run id, the status and an
/// error message — and a run reads none of it. Loading one per tracked object cost roughly 110 bytes
/// each in fields nothing looks at.
/// <para>
/// At the scale this product targets that is not a rounding error: a million-object database holds a
/// million of these for the whole of its pass, and this is the structure a no-change run pays for
/// while a first run does not — which is why steady-state peak memory measured HIGHER than the
/// initial scrape. The repository's query already selected exactly these six columns; only the shape
/// it materialised them into was wrong.
/// </para>
/// <para>
/// Schema names are pooled when this is loaded. A database of a million objects typically has a
/// handful of schemas, so the alternative is a million separate copies of "dbo".
/// </para>
/// </remarks>
public sealed class TrackedObjectSnapshot
{
    /// <summary>Row id, used to delete the state of an object that is gone.</summary>
    public long Id { get; init; }

    public SqlObjectType ObjectType { get; init; }

    public string SchemaName { get; init; } = string.Empty;

    public string ObjectName { get; init; } = string.Empty;

    /// <summary>
    /// Repository-relative path of the object's file, as recorded. Kept rather than recomputed
    /// because a difference between this and where the object maps TODAY is exactly how a layout
    /// change is detected and the stale file cleaned up.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>The content hash this object was last scripted to.</summary>
    public string LastHash { get; init; } = string.Empty;
}
