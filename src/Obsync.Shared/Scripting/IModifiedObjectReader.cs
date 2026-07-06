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
    /// </summary>
    Task<IReadOnlyList<ModifiedObjectSnapshotItem>> GetSnapshotAsync(
        SqlConnectionProfile profile, string? password, string database,
        IReadOnlyCollection<SqlObjectType> types, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default);
}
