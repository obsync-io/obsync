using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Engine;

/// <summary>One snapshot object the incremental pass skips, with the prior state that stands in for it.</summary>
internal sealed record IncrementalSkip(ModifiedObjectSnapshotItem Item, TrackedObjectState PriorState);

/// <summary>The pure output of <see cref="IncrementalPlanner.Plan"/> for one database.</summary>
internal sealed record IncrementalPlan(
    IReadOnlyList<IncrementalSkip> SkippedItems,
    IReadOnlySet<SqlObjectType> FilterableTypes,
    IReadOnlyDictionary<SqlObjectType, DateTime> NewWatermarks,
    IReadOnlyList<ModifiedObjectSnapshotItem> IgnoredItems);

/// <summary>
/// The pure heart of incremental scripting: given a modification snapshot, the prior object
/// states, and the stored per-type watermarks, decides which objects can skip re-scripting,
/// which types the providers may filter by <c>modify_date</c>, and what the next watermarks are.
/// All <c>modify_date</c> values are opaque server-local datetimes compared verbatim.
/// </summary>
internal static class IncrementalPlanner
{
    /// <summary>
    /// The types whose catalog rows live in <c>sys.objects</c> with a reliable
    /// <c>modify_date</c>. Every other type is always fully scanned (they are few).
    /// </summary>
    internal static readonly IReadOnlySet<SqlObjectType> CapableTypes = new HashSet<SqlObjectType>
    {
        SqlObjectType.Table,
        SqlObjectType.View,
        SqlObjectType.StoredProcedure,
        SqlObjectType.Function,
        SqlObjectType.Trigger,
        SqlObjectType.Synonym,
        SqlObjectType.Sequence,
    };

    /// <summary>
    /// Plans one database's incremental run.
    /// <list type="bullet">
    /// <item><c>SkippedItems</c> — objects of a filterable type with a watermark, a
    /// <c>modify_date</c> strictly older than it, a prior state, and no ignore-rule match.</item>
    /// <item><c>FilterableTypes</c> — watermarked types with no safety violation. A violation is
    /// an old (<c>modify_date &lt; watermark</c>), un-ignored object with NO prior state — e.g. the
    /// schema filter or selection changed and an old object is newly in scope; filtering would
    /// silently never script it, so that type gets a full scan this run instead.</item>
    /// <item><c>NewWatermarks</c> — the per-type max <c>modify_date</c> across the snapshot; a
    /// type with no snapshot rows is absent (it keeps its old watermark).</item>
    /// </list>
    /// Providers filter with <c>modify_date &gt;= watermark</c> (not <c>&gt;</c>), so an object
    /// modified within the same 3.33ms tick as the previous snapshot's max is re-read — that
    /// closes the boundary race at the cost of re-scripting a handful of boundary objects.
    /// </summary>
    internal static IncrementalPlan Plan(
        IReadOnlyList<ModifiedObjectSnapshotItem> snapshot,
        IReadOnlyDictionary<string, TrackedObjectState> priorStatesByKey,
        IReadOnlyDictionary<SqlObjectType, DateTime> watermarks,
        Func<SqlObjectType, string, string, bool> isIgnored)
    {
        var newWatermarks = new Dictionary<SqlObjectType, DateTime>();
        var violatedTypes = new HashSet<SqlObjectType>();
        var candidates = new List<IncrementalSkip>();
        var ignored = new List<ModifiedObjectSnapshotItem>();

        foreach (var item in snapshot)
        {
            if (!newWatermarks.TryGetValue(item.Type, out var max) || item.ModifyDate > max)
            {
                newWatermarks[item.Type] = item.ModifyDate;
            }

            // Ignored/out-of-filter objects are never scripted, but their committed files are
            // deliberately retained. They must be reported REGARDLESS of modify_date — this check
            // sits before the watermark cutoff because a recently-modified out-of-filter object is
            // still out of scope, and dropping it here made the deletion pass treat it as a
            // dropped object and delete its committed file.
            if (isIgnored(item.Type, item.Schema, item.Name))
            {
                ignored.Add(item);
                continue;
            }

            // Only objects strictly older than the type's watermark are skip/violation candidates;
            // a boundary modify_date == watermark is re-read (see the >= comparison above).
            if (!watermarks.TryGetValue(item.Type, out var watermark) || item.ModifyDate >= watermark)
            {
                continue;
            }

            if (priorStatesByKey.TryGetValue(StateKey(item), out var prior))
            {
                candidates.Add(new IncrementalSkip(item, prior));
            }
            else
            {
                violatedTypes.Add(item.Type);
            }
        }

        var filterable = watermarks.Keys.Where(t => !violatedTypes.Contains(t)).ToHashSet();
        // A violated type is fully scanned, so none of its objects may be pre-marked as seen —
        // the provider will yield them again and they must go through the normal apply path once.
        var skipped = candidates.Where(c => filterable.Contains(c.Item.Type)).ToList();

        return new IncrementalPlan(skipped, filterable, newWatermarks, ignored);
    }

    /// <summary>The engine's object-state key format — must match <c>SyncEngine.StateKey</c> exactly.</summary>
    internal static string StateKey(ModifiedObjectSnapshotItem item) => $"{(int)item.Type}|{item.Schema}|{item.Name}";
}
