using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Shared.Scripting;

/// <summary>
/// One row of a modification snapshot: an object's identity plus its <c>sys.objects.modify_date</c>.
/// The date is the server-local catalog value, treated as an opaque monotonic watermark — it is
/// stored and compared verbatim, never converted between time zones.
/// </summary>
public sealed record ModifiedObjectSnapshotItem(SqlObjectType Type, string Schema, string Name, DateTime ModifyDate);

/// <summary>
/// Reads a lightweight modification snapshot of a database — every object of the requested types
/// with its <c>modify_date</c> — in one bulk catalog query. The incremental-scripting planner
/// compares the snapshot against stored watermarks to decide which objects can skip re-scripting.
/// </summary>
public interface IModifiedObjectReader
{
    /// <summary>
    /// Returns one snapshot item per catalog object of the requested types. Only types whose
    /// catalog rows live in <c>sys.objects</c> with a reliable <c>modify_date</c> may be requested.
    /// A non-empty <paramref name="schemaFilter"/> narrows the snapshot server-side to those
    /// schemas — safe because out-of-filter objects are out of the run's scope entirely (the
    /// engine retains their committed files independently of the snapshot, and a widened filter
    /// brings them back as no-prior planner violations that force the full scan).
    /// </summary>
    Task<IReadOnlyList<ModifiedObjectSnapshotItem>> GetSnapshotAsync(
        SqlConnectionProfile profile, string? password, string database,
        IReadOnlyCollection<SqlObjectType> types, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, IReadOnlyCollection<string>? schemaFilter = null,
        CancellationToken cancellationToken = default);
}
