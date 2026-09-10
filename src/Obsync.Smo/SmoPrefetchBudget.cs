using Obsync.Shared.Objects;

namespace Obsync.Smo;

/// <summary>
/// The memory bound on SMO's bulk child-metadata prefetch.
///
/// Prefetch is all-or-nothing by construction, not by choice. <c>Database.PrefetchObjects</c> takes
/// a database and a CLR type and nothing else — there is no overload that narrows it to a subset of
/// the collection — so it always bulk-loads child metadata (columns, indexes, constraints, foreign
/// keys, triggers, statistics, extended properties) for EVERY object of that type in the database,
/// and that metadata stays attached to the SMO objects for the life of the connection. SMO's own
/// large-database path batches the QUERIES it issues and never releases what they loaded, so there
/// is no vendor mechanism for "prefetch a slice, script it, release it" to build on. Past the
/// budget, scripting therefore stays lazy — correct, but an order of magnitude slower.
///
/// The budget replaces a bare object-count ceiling that contradicted its own justification: the
/// ceiling's comment priced prefetch as bytes-per-object × objects × SLICES (each slice connection
/// caches its own copy), but the gate only ever compared the object count, so the same 25,000
/// objects were allowed whether they were about to be prefetched onto one connection or eight. The
/// budget is set to exactly what that ceiling already sanctioned at maximum fan-out, so this bounds
/// the same peak it always did — it just spends it honestly, allowing far larger collections when
/// the run is not fanning scripting out eight ways.
/// </summary>
internal static class SmoPrefetchBudget
{
    /// <summary>Cached child metadata per prefetched object, measured (~7 KB per table).</summary>
    internal const int BytesPerObject = 7 * 1024;

    /// <summary>
    /// The peak prefetch footprint a run may hold, across all of its scripting connections. Written
    /// as the product it came from: the previous 25,000-object ceiling at the engine's maximum
    /// scripting fan-out of 8 connections. Raising this raises the memory a single database sweep
    /// can pin, so change it deliberately.
    /// </summary>
    internal const long BudgetBytes = 25_000L * BytesPerObject * 8;

    /// <summary>Bytes a prefetch of <paramref name="inCollection"/> objects would hold across every slice.</summary>
    internal static long EstimateBytes(int inCollection, int sliceCount) =>
        (long)Math.Max(0, inCollection) * BytesPerObject * Math.Max(1, sliceCount);

    /// <summary>Whether prefetching a collection of this size onto this many connections fits the budget.</summary>
    internal static bool Fits(int inCollection, int sliceCount) =>
        EstimateBytes(inCollection, sliceCount) <= BudgetBytes;

    /// <summary>The largest collection that still fits when the work is spread over this many connections.</summary>
    internal static int MaxObjects(int sliceCount) =>
        (int)(BudgetBytes / (BytesPerObject * (long)Math.Max(1, sliceCount)));
}

/// <summary>
/// Reported once per type when a collection was too large to bulk-prefetch, so the run scripted it
/// lazily instead. This exists because crossing the limit was completely silent: the run simply took
/// far longer with nothing anywhere saying why. <see cref="Obsync.Smo.SmoScriptProvider"/> cannot say
/// so itself — this assembly has an <c>ILogger</c> and no access to the run log — so it hands the
/// caller a finished sentence to record.
/// </summary>
/// <param name="Database">The database being scripted, so a caller can attribute the report.</param>
/// <param name="Type">The object type whose prefetch was skipped.</param>
/// <param name="ObjectsInCollection">Objects of that type in the database — what prefetch would have loaded.</param>
/// <param name="SliceCount">Scripting connections the work was spread over; each would cache its own copy.</param>
/// <param name="EstimatedBytes">What prefetching would have held across those connections.</param>
/// <param name="BudgetBytes">The limit it exceeded.</param>
/// <param name="MaxObjectsAtThisFanOut">The largest collection that would still have fit at this fan-out.</param>
public sealed record SmoPrefetchLimit(
    string Database,
    SqlObjectType Type,
    int ObjectsInCollection,
    int SliceCount,
    long EstimatedBytes,
    long BudgetBytes,
    int MaxObjectsAtThisFanOut)
{
    /// <summary>
    /// A complete, self-explanatory run-log line. It states the fact, states that output is
    /// unaffected, and quotes the only speed figure this codebase has actually measured (a
    /// 2,000-table sweep: 230s lazy against 54s prefetched) rather than inventing one for this
    /// estate. It does not recommend lowering parallelism — that trade has not been measured —
    /// it only explains why the fan-out is part of the arithmetic.
    /// </summary>
    public string Message =>
        $"Bulk metadata prefetch was skipped for {Type} in {Database}: {ObjectsInCollection:N0} objects " +
        $"across {SliceCount} scripting connection(s) would hold about {Describe(EstimatedBytes)} of cached " +
        $"child metadata, over the {Describe(BudgetBytes)} limit. Scripting produces identical output but " +
        $"reads each object's metadata individually, which measured about 4x slower on a 2,000-table sweep. " +
        $"About {MaxObjectsAtThisFanOut:N0} objects would still fit at this fan-out — each scripting " +
        $"connection caches its own copy, so the limit falls as scripting fans out.";

    private static string Describe(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (double)(1024L * 1024 * 1024):0.#} GB"
            : $"{bytes / (double)(1024 * 1024):0.#} MB";
}
