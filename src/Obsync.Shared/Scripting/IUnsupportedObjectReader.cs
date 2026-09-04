using Obsync.Shared.Models;

namespace Obsync.Shared.Scripting;

/// <summary>How many objects of one type Obsync does not script exist in a database.</summary>
public sealed record UnsupportedObjectGroup(string TypeName, int Count);

/// <summary>
/// Counts objects of types Obsync cannot script, so their absence from the repository is reported
/// rather than silent.
///
/// Obsync scripts an enumerated set of object types. An object outside it is unreachable at every
/// stage — selection resolves only catalogued types, the providers query only for the types they
/// were handed, and the inventory is built from what was scripted rather than from the database — so
/// a database full of Service Broker queues and certificates produced a run reporting zero skips and
/// zero warnings. Silence was indistinguishable from complete coverage.
/// </summary>
public interface IUnsupportedObjectReader
{
    /// <summary>
    /// Groups, by type name, the objects in <paramref name="database"/> that Obsync does not script.
    /// Best-effort by contract: an implementation returns what it could count and never throws, so a
    /// coverage report can never be the reason a run fails.
    /// </summary>
    Task<IReadOnlyList<UnsupportedObjectGroup>> ReadAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default);
}
