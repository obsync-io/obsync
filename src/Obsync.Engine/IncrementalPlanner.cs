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
    IReadOnlyDictionary<SqlObjectType, ScriptingWatermark> NewWatermarks,
    IReadOnlyList<ModifiedObjectSnapshotItem> IgnoredItems,
    IReadOnlyDictionary<SqlObjectType, WatermarkInvalidation> Invalidated);

/// <summary>Why a stored watermark could not be trusted this run.</summary>
internal enum WatermarkInvalidation
{
    /// <summary>
    /// Obsync would no longer emit the bytes the stored hashes were taken over - the normalizer, the
    /// repository layout, an emission-affecting job setting, or the SMO library changed.
    /// </summary>
    EmissionChanged,

    /// <summary>
    /// The database's <c>modify_date</c> timeline moved backwards, which means this is not the
    /// database the watermark was taken from: a restore from backup, or a refresh from another
    /// environment.
    /// </summary>
    TimelineMovedBackwards,
}

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
    /// silently never script it, so that type gets a full scan this run instead. An object marked
    /// <c>DefinitionUnavailable</c> is exempt: a CLR or <c>WITH ENCRYPTION</c> module can never
    /// acquire a prior state, so counting it would violate its type on every run forever — turning
    /// a rule meant to catch a one-off scope change into a permanent full scan of every procedure
    /// and function definition, which is the very cost the watermark exists to avoid.</item>
    /// <item><c>NewWatermarks</c> — the per-type max <c>modify_date</c> across the snapshot; a
    /// type with no snapshot rows is absent (it keeps its old watermark).</item>
    /// </list>
    /// Providers filter with <c>modify_date &gt;= watermark</c> (not <c>&gt;</c>), so an object
    /// modified within the same 3.33ms tick as the previous snapshot's max is re-read — that
    /// closes the boundary race at the cost of re-scripting a handful of boundary objects.
    /// </summary>
    /// <param name="scannedTypes">
    /// The types this run is actually scanning. Filterability is derived from it, so a type that is
    /// not being scanned cannot appear in <c>FilterableTypes</c> at all.
    /// <para>
    /// This parameter is the fix for a whole class of defect rather than one instance of it.
    /// Filterability used to be derived from the stored watermark KEYS, which are written as upserts
    /// and never deleted — so a type the caller had withheld from the scan (because the branch
    /// diverged) still carried a watermark row, and the planner duly offered a filter for it. The
    /// caller then had to remember to intersect the result with what it was scanning. It did not,
    /// and the type the engine had just decided to re-scan in full was filtered HARDER than usual:
    /// its unchanged objects never reached the engine, were never marked seen, and the deletion pass
    /// read every one of them as dropped.
    /// </para>
    /// <para>
    /// Two sources of truth for one fact is what made that expressible. There is now one.
    /// </para>
    /// </param>
    internal static IncrementalPlan Plan(
        IReadOnlyList<ModifiedObjectSnapshotItem> snapshot,
        IReadOnlyDictionary<string, TrackedObjectState> priorStatesByKey,
        IReadOnlyDictionary<SqlObjectType, ScriptingWatermark> stored,
        IReadOnlySet<SqlObjectType> scannedTypes,
        string emissionFingerprint,
        Func<SqlObjectType, string, string, bool> isIgnored)
    {
        // Pass 1: the snapshot's high-water mark per type, and WHICH object holds it. That identity
        // is what lets the next pass tell a restored database from a dropped object.
        var snapshotMax = new Dictionary<SqlObjectType, DateTime>();
        var sentinelKeys = new Dictionary<SqlObjectType, string>();
        var sentinelDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot)
        {
            var key = StateKey(item);
            sentinelDates[key] = item.ModifyDate;
            if (!snapshotMax.TryGetValue(item.Type, out var currentMax) || item.ModifyDate > currentMax)
            {
                snapshotMax[item.Type] = item.ModifyDate;
                sentinelKeys[item.Type] = key;
            }
        }

        // Pass 2: decide which stored watermarks may still be trusted. Every check fails toward
        // WORK - an unverifiable precondition means one full scan of that type, which repairs the
        // state and re-stamps the record. A full scan is expensive; a silent skip is wrong.
        var invalidated = new Dictionary<SqlObjectType, WatermarkInvalidation>();
        var watermarks = new Dictionary<SqlObjectType, DateTime>();
        foreach (var (storedType, record) in stored)
        {
            // A null fingerprint predates the column and is adopted rather than invalidated, so
            // upgrading does not force a full scan of every type on a large estate.
            if (record.Fingerprint is not null && record.Fingerprint != emissionFingerprint)
            {
                invalidated[storedType] = WatermarkInvalidation.EmissionChanged;
                continue;
            }

            if (MovedBackwards(storedType, record))
            {
                invalidated[storedType] = WatermarkInvalidation.TimelineMovedBackwards;
                continue;
            }

            watermarks[storedType] = record.Value;
        }

        var violatedTypes = new HashSet<SqlObjectType>();
        var candidates = new List<IncrementalSkip>();
        var ignored = new List<ModifiedObjectSnapshotItem>();

        foreach (var item in snapshot)
        {

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
            else if (!item.DefinitionUnavailable)
            {
                violatedTypes.Add(item.Type);
            }
        }

        var filterable = watermarks.Keys
            .Where(t => scannedTypes.Contains(t) && !violatedTypes.Contains(t))
            .ToHashSet();
        // A violated type is fully scanned, so none of its objects may be pre-marked as seen —
        // the provider will yield them again and they must go through the normal apply path once.
        var skipped = candidates.Where(c => filterable.Contains(c.Item.Type)).ToList();

        var newWatermarks = snapshotMax.ToDictionary(
            pair => pair.Key,
            pair => new ScriptingWatermark(
                // Never silently LOWER a watermark that is still trusted. Persistence is a blind
                // upsert, and the new value used to be the snapshot maximum computed from an empty
                // dictionary - so a restored database quietly rewrote the watermark DOWN to its own
                // maximum, concealing the very timeline move that should have invalidated it. A
                // watermark now only moves down when it has been invalidated, which is exactly when
                // the type was scanned in full and the lower value is the truthful one.
                invalidated.ContainsKey(pair.Key) || !stored.TryGetValue(pair.Key, out var previous)
                    ? pair.Value
                    : (previous.Value >= pair.Value ? previous.Value : pair.Value),
                emissionFingerprint,
                sentinelKeys.GetValueOrDefault(pair.Key)),
            EqualityComparer<SqlObjectType>.Default);

        return new IncrementalPlan(skipped, filterable, newWatermarks, ignored, invalidated);

        bool MovedBackwards(SqlObjectType type, ScriptingWatermark record)
        {
            // The precise test: the object that SET this watermark is still here, and its date has
            // gone BACKWARDS. A date moving forward is an ordinary edit - testing for "changed"
            // rather than "moved backwards" would invalidate on every normal modification of the
            // newest object, which is the most frequently modified object there is.
            if (record.SentinelKey is null)
            {
                // A row written before sentinels existed. There is nothing reliable to compare: the
                // type's maximum sitting below the watermark is equally consistent with a restore and
                // with the newest object having been dropped at some point in the past, and on a
                // large estate guessing "restore" would mean a full scan of every type on the first
                // run after upgrading. Adopted, exactly as a null fingerprint is - and the very next
                // run writes a sentinel, so the blind spot is one run wide.
                return false;
            }

            if (sentinelDates.TryGetValue(record.SentinelKey, out var sentinelNow))
            {
                return sentinelNow < record.Value;
            }

            // The sentinel is gone. Fall back to comparing the type's maximum, which cannot tell a
            // restore from the newest object simply being dropped - so it errs toward the full scan.
            // A type with no rows at all keeps its watermark: an empty snapshot is not evidence that
            // anything moved.
            return snapshotMax.TryGetValue(type, out var max) && max < record.Value;
        }
    }

    /// <summary>The engine's object-state key format — must match <c>SyncEngine.StateKey</c> exactly.</summary>
    internal static string StateKey(ModifiedObjectSnapshotItem item) => $"{(int)item.Type}|{item.Schema}|{item.Name}";
}
