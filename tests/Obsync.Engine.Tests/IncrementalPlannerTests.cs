using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Engine.Tests;

/// <summary>
/// Exhaustive coverage of the pure incremental planner: the skip rule (watermark + prior +
/// not-ignored + strictly older), the boundary tick, the safety violation that forces a full
/// scan, and the new-watermark computation.
/// </summary>
public sealed class IncrementalPlannerTests
{
    private static readonly DateTime Watermark = new(2026, 7, 1, 12, 0, 0);
    private static readonly DateTime Older = Watermark.AddMinutes(-5);
    private static readonly DateTime Newer = Watermark.AddMinutes(5);

    private static ModifiedObjectSnapshotItem Item(
        SqlObjectType type, string name, DateTime modifyDate, string schema = "dbo") =>
        new(type, schema, name, modifyDate);

    private static Dictionary<string, TrackedObjectSnapshot> Prior(params ModifiedObjectSnapshotItem[] items) =>
        items.ToDictionary(
            i => $"{(int)i.Type}|{i.Schema}|{i.Name}",
            i => new TrackedObjectSnapshot
            {
                ObjectType = i.Type,
                SchemaName = i.Schema,
                ObjectName = i.Name,
                FilePath = $"env/db/x/{i.Schema}.{i.Name}.sql",
                LastHash = $"hash-{i.Name}",
            },
            StringComparer.OrdinalIgnoreCase);

    private const string Fingerprint = "fp-current";

    private static Dictionary<SqlObjectType, ScriptingWatermark> Watermarks(params SqlObjectType[] types) =>
        types.ToDictionary(t => t, _ => new ScriptingWatermark(Watermark, Fingerprint));

    /// <summary>A module the server will never return a definition for: CLR, or WITH ENCRYPTION.</summary>
    private static ModifiedObjectSnapshotItem Unscriptable(
        SqlObjectType type, string name, DateTime modifyDate, string schema = "dbo") =>
        new(type, schema, name, modifyDate, DefinitionUnavailable: true);

    private static bool NotIgnored(SqlObjectType type, string schema, string name) => false;

    /// <summary>
    /// The ordinary case: every capable type is being scanned. Tests that care about the scanned set
    /// call <see cref="IncrementalPlanner.Plan"/> directly.
    /// </summary>
    private static IncrementalPlan Plan(
        IReadOnlyList<ModifiedObjectSnapshotItem> snapshot,
        IReadOnlyDictionary<string, TrackedObjectSnapshot> prior,
        IReadOnlyDictionary<SqlObjectType, ScriptingWatermark> watermarks,
        Func<SqlObjectType, string, string, bool> isIgnored) =>
        IncrementalPlanner.Plan(
            snapshot, prior, watermarks, IncrementalPlanner.CapableTypes, Fingerprint, isIgnored);

    [Fact]
    public void Skips_OlderObject_WithWatermarkAndPriorState()
    {
        var item = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);

        var plan = Plan([item], Prior(item), Watermarks(SqlObjectType.StoredProcedure), NotIgnored);

        var skip = Assert.Single(plan.SkippedItems);
        Assert.Equal(item, skip.Item);
        Assert.Equal("hash-usp_Old", skip.PriorState.LastHash);
        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
    }

    [Fact]
    public void DoesNotSkip_WhenTheTypeHasNoWatermark()
    {
        var item = Item(SqlObjectType.View, "vw_Old", Older);

        var plan = Plan([item], Prior(item), Watermarks(), NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Empty(plan.FilterableTypes); // no watermark → nothing filterable either
    }

    [Fact]
    public void DoesNotSkip_ObjectModifiedOnTheWatermarkBoundary()
    {
        // modify_date == watermark must be re-read: the provider filter is >=, closing the
        // race where an object changes within the snapshot's last 3.33ms tick.
        var item = Item(SqlObjectType.View, "vw_Boundary", Watermark);

        var plan = Plan([item], Prior(item), Watermarks(SqlObjectType.View), NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Contains(SqlObjectType.View, plan.FilterableTypes); // boundary is no violation
    }

    [Fact]
    public void DoesNotSkip_ModifiedObject()
    {
        var item = Item(SqlObjectType.Table, "Orders", Newer);

        var plan = Plan([item], Prior(item), Watermarks(SqlObjectType.Table), NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Contains(SqlObjectType.Table, plan.FilterableTypes);
    }

    [Fact]
    public void DoesNotSkip_IgnoredObject_AndIgnoredObjectIsNoViolation()
    {
        // An old object with no prior state would be a violation — unless it is ignored, in
        // which case the providers never yield it anyway.
        var ignored = Item(SqlObjectType.Table, "Ignored", Older);

        var plan = Plan(
            [ignored],
            new Dictionary<string, TrackedObjectSnapshot>(StringComparer.OrdinalIgnoreCase),
            Watermarks(SqlObjectType.Table),
            (_, _, name) => name == "Ignored");

        Assert.Empty(plan.SkippedItems);
        Assert.Contains(SqlObjectType.Table, plan.FilterableTypes);
    }

    [Fact]
    public void IgnoredObject_WithPriorState_IsReportedAsIgnored_SoItsCommittedFileIsRetained()
    {
        // Regression: the planner used to drop old ignored objects entirely — never marked seen —
        // so the deletion pass treated them as dropped objects and DELETED their committed files,
        // but only when incremental filtering kicked in. The full-scan path retains them (it marks
        // seen before the ignore check); the planner must report them so the engine can do the same.
        var ignored = Item(SqlObjectType.StoredProcedure, "usp_Ignored", Older);
        var normal = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);

        var plan = Plan(
            [ignored, normal],
            Prior(ignored, normal),
            Watermarks(SqlObjectType.StoredProcedure),
            (_, _, name) => name == "usp_Ignored");

        var reported = Assert.Single(plan.IgnoredItems);
        Assert.Equal(ignored, reported);
        var skip = Assert.Single(plan.SkippedItems); // ignored is not in SkippedItems (not inventoried)
        Assert.Equal(normal, skip.Item);
        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
    }

    [Fact]
    public void OldObjectWithoutPriorState_RemovesItsTypeFromFilterableTypes_AndItsSkips()
    {
        // "usp_NewlyInScope" is old but was never scripted (e.g. the selection changed) —
        // filtering StoredProcedure would silently never script it, so the whole type gets a
        // full scan this run: it is not filterable and none of its objects are pre-skipped.
        var skippable = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);
        var violation = Item(SqlObjectType.StoredProcedure, "usp_NewlyInScope", Older);
        var otherType = Item(SqlObjectType.View, "vw_Old", Older);

        var plan = Plan(
            [skippable, violation, otherType],
            Prior(skippable, otherType),
            Watermarks(SqlObjectType.StoredProcedure, SqlObjectType.View),
            NotIgnored);

        Assert.DoesNotContain(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        Assert.Contains(SqlObjectType.View, plan.FilterableTypes);
        var skip = Assert.Single(plan.SkippedItems); // the violated type's candidate is dropped too
        Assert.Equal(otherType, skip.Item);
    }

    /// <summary>
    /// A CLR or WITH ENCRYPTION module can never acquire a prior state, so counting it as a
    /// violation forced a full scan of every procedure and function definition on every run,
    /// forever — the rising watermark guarantees it stays old. The violation rule exists to catch a
    /// one-off scope change, not to be permanently tripped by an object that will never be
    /// scriptable.
    /// </summary>
    [Fact]
    public void OldUnscriptableModuleWithoutPriorState_DoesNotViolateItsType()
    {
        var skippable = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);
        var clr = Unscriptable(SqlObjectType.StoredProcedure, "usp_ClrProc", Older);

        var plan = Plan(
            [skippable, clr],
            Prior(skippable),
            Watermarks(SqlObjectType.StoredProcedure),
            NotIgnored);

        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        // The scriptable object is still pre-skipped, which is the whole point: the fast path holds.
        var skip = Assert.Single(plan.SkippedItems);
        Assert.Equal(skippable, skip.Item);
    }

    /// <summary>
    /// The exemption must be narrow. An ordinary object with no prior state still violates, or the
    /// rule that stops a newly-in-scope object from being silently skipped forever is lost.
    /// </summary>
    [Fact]
    public void TheExemptionAppliesOnlyToUnscriptableObjects()
    {
        var clr = Unscriptable(SqlObjectType.StoredProcedure, "usp_ClrProc", Older);
        var newlyInScope = Item(SqlObjectType.Function, "fn_NewlyInScope", Older);

        var plan = Plan(
            [clr, newlyInScope],
            Prior(),
            Watermarks(SqlObjectType.StoredProcedure, SqlObjectType.Function),
            NotIgnored);

        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        Assert.DoesNotContain(SqlObjectType.Function, plan.FilterableTypes);
    }

    /// <summary>
    /// An unscriptable module that somehow does have a prior state keeps the ordinary skip path —
    /// the exemption changes who violates, not who is skippable.
    /// </summary>
    [Fact]
    public void UnscriptableObjectWithPriorState_IsStillASkipCandidate()
    {
        var clr = Unscriptable(SqlObjectType.StoredProcedure, "usp_ClrProc", Older);

        var plan = Plan(
            [clr],
            Prior(clr),
            Watermarks(SqlObjectType.StoredProcedure),
            NotIgnored);

        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        Assert.Equal(clr, Assert.Single(plan.SkippedItems).Item);
    }

    /// <summary>An unscriptable module still counts toward its type's new watermark.</summary>
    [Fact]
    public void UnscriptableObject_StillContributesToTheNewWatermark()
    {
        var plan = Plan(
            [Unscriptable(SqlObjectType.StoredProcedure, "usp_ClrProc", Newer)],
            Prior(),
            Watermarks(SqlObjectType.StoredProcedure),
            NotIgnored);

        Assert.Equal(Newer, plan.NewWatermarks[SqlObjectType.StoredProcedure].Value);
    }

    /// <summary>
    /// An ignored unscriptable module takes the ignore path unchanged — the ignore check runs
    /// first and is deliberately independent of modify_date.
    /// </summary>
    [Fact]
    public void IgnoredUnscriptableObject_IsStillReportedAsIgnored()
    {
        var clr = Unscriptable(SqlObjectType.StoredProcedure, "usp_ClrProc", Older);

        var plan = Plan(
            [clr],
            Prior(),
            Watermarks(SqlObjectType.StoredProcedure),
            (_, _, name) => name == "usp_ClrProc");

        Assert.Equal(clr, Assert.Single(plan.IgnoredItems));
        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
    }

    [Fact]
    public void NewWatermarks_AreThePerTypeMax_AndAbsentForTypesWithoutSnapshotRows()
    {
        var plan = Plan(
            [
                Item(SqlObjectType.Table, "A", Older),
                Item(SqlObjectType.Table, "B", Newer),
                Item(SqlObjectType.View, "C", Watermark),
            ],
            new Dictionary<string, TrackedObjectSnapshot>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<SqlObjectType, ScriptingWatermark>(),
            NotIgnored);

        Assert.Equal(Newer, plan.NewWatermarks[SqlObjectType.Table].Value);
        Assert.Equal(Watermark, plan.NewWatermarks[SqlObjectType.View].Value);
        Assert.False(plan.NewWatermarks.ContainsKey(SqlObjectType.StoredProcedure));
    }

    [Fact]
    public void EmptySnapshot_KeepsWatermarkedTypesFilterable_AndProducesNoNewWatermarks()
    {
        var plan = Plan(
            [],
            new Dictionary<string, TrackedObjectSnapshot>(StringComparer.OrdinalIgnoreCase),
            Watermarks(SqlObjectType.Synonym),
            NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Empty(plan.NewWatermarks); // the old watermark is kept by not being overwritten
        Assert.Contains(SqlObjectType.Synonym, plan.FilterableTypes);
    }

    [Fact]
    public void StateKey_MatchesTheEngineFormat()
    {
        var item = Item(SqlObjectType.StoredProcedure, "usp_GetCustomer", Older);

        Assert.Equal("45|dbo|usp_GetCustomer", IncrementalPlanner.StateKey(item));
    }

    [Fact]
    public void CapableTypes_AreExactlyTheSysObjectsBackedTypes()
    {
        Assert.Equal(
            new HashSet<SqlObjectType>
            {
                SqlObjectType.Table, SqlObjectType.View, SqlObjectType.StoredProcedure,
                SqlObjectType.Function, SqlObjectType.Trigger, SqlObjectType.Synonym, SqlObjectType.Sequence,
            },
            IncrementalPlanner.CapableTypes);
    }
    /// <summary>
    /// A type the caller withheld from the scan must not come back as filterable. Filterability used
    /// to be derived from the stored watermark KEYS, and watermark writes are upserts that never
    /// delete a row — so a type withheld for divergence still had one, and the planner offered a
    /// filter for the very type the engine had just decided to re-scan in full. The caller was left
    /// to remember the intersection.
    ///
    /// The consequence was the opposite of the intent: the withheld type was filtered HARDER than
    /// normal, its unchanged objects never reached the engine, they were never marked seen, and the
    /// deletion pass read every one of them as dropped.
    /// </summary>
    [Fact]
    public void AWithheldType_IsNotOfferedItsOwnWatermarkBack()
    {
        var procedure = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);
        var view = Item(SqlObjectType.View, "v_Old", Older);

        // Both types carry a stored watermark; only the view is being scanned this run.
        var plan = IncrementalPlanner.Plan(
            [procedure, view],
            Prior(procedure, view),
            Watermarks(SqlObjectType.StoredProcedure, SqlObjectType.View),
            new HashSet<SqlObjectType> { SqlObjectType.View },
            Fingerprint,
            NotIgnored);

        Assert.DoesNotContain(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        Assert.Contains(SqlObjectType.View, plan.FilterableTypes);

        // ...and nothing of the withheld type may be pre-marked as skipped either, or the deletion
        // pass would read those objects as dropped.
        Assert.DoesNotContain(plan.SkippedItems, skip => skip.Item.Type == SqlObjectType.StoredProcedure);
    }

    /// <summary>
    /// The bootstrap: a first run has no prior state and therefore nothing to skip, but it must
    /// still produce watermarks. Staging them only when there was prior state cost an entire extra
    /// run — run 1 stored nothing, run 2 was a full scrape whose only product was the first
    /// watermarks, and run 3 was the earliest run that could skip anything.
    /// </summary>
    [Fact]
    public void AFirstRun_SkipsNothing_ButStillProducesWatermarks()
    {
        var item = Item(SqlObjectType.StoredProcedure, "usp_New", Newer);

        var plan = IncrementalPlanner.Plan(
            [item],
            new Dictionary<string, TrackedObjectSnapshot>(),
            new Dictionary<SqlObjectType, ScriptingWatermark>(),
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Empty(plan.FilterableTypes);
        Assert.Equal(Newer, plan.NewWatermarks[SqlObjectType.StoredProcedure].Value);
    }
    // ---- P2: the modify_date timeline only moves forward ------------------------------------
    //
    // modify_date travels with the database. A restore from backup, or a prod-to-UAT refresh, moves
    // every value backwards -- below a watermark already persisted. Every affected object is then
    // "older than the watermark", so it is skipped with no file read and no hash check, while the
    // job reports No changes and the repository holds the wrong scripts.
    //
    // It was worse than a permanent skip. The new watermark was the snapshot maximum computed from
    // an empty dictionary and persisted by blind upsert, so the restored database rewrote the
    // watermark DOWN to its own maximum -- erasing the evidence of the move on the very run that
    // should have caught it.

    private static ScriptingWatermark Sentinelled(DateTime value, ModifiedObjectSnapshotItem sentinel) =>
        new(value, Fingerprint, $"{(int)sentinel.Type}|{sentinel.Schema}|{sentinel.Name}");

    [Fact]
    public void ARestoredDatabase_IsNotTrusted_WhenTheSentinelMovedBackwards()
    {
        // The sentinel is the object whose modify_date set the watermark. After a restore it is
        // still there, but its date now predates the watermark it established.
        var sentinel = Item(SqlObjectType.StoredProcedure, "usp_Newest", Watermark);
        var other = Item(SqlObjectType.StoredProcedure, "usp_Other", Older);
        var restoredSentinel = Item(SqlObjectType.StoredProcedure, "usp_Newest", Older);

        var plan = IncrementalPlanner.Plan(
            [restoredSentinel, other],
            Prior(sentinel, other),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                [SqlObjectType.StoredProcedure] = Sentinelled(Watermark, sentinel),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);

        Assert.Equal(
            WatermarkInvalidation.TimelineMovedBackwards,
            plan.Invalidated[SqlObjectType.StoredProcedure]);
        Assert.Empty(plan.SkippedItems);
        Assert.DoesNotContain(SqlObjectType.StoredProcedure, plan.FilterableTypes);

        // The re-baseline is the restored timeline's own maximum -- it was scanned in full, so the
        // lower value is now the truthful one.
        Assert.Equal(Older, plan.NewWatermarks[SqlObjectType.StoredProcedure].Value);
    }

    [Fact]
    public void AnOrdinaryEditOfTheNewestObject_IsNotMistakenForARestore()
    {
        // The sentinel's date moving FORWARD is the most ordinary event there is: the newest object
        // is the one most likely to be edited next. Testing for "changed" rather than "moved
        // backwards" would invalidate the watermark on almost every run.
        var sentinel = Item(SqlObjectType.StoredProcedure, "usp_Newest", Watermark);
        var edited = Item(SqlObjectType.StoredProcedure, "usp_Newest", Newer);
        var old = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);

        var plan = IncrementalPlanner.Plan(
            [edited, old],
            Prior(sentinel, old),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                [SqlObjectType.StoredProcedure] = Sentinelled(Watermark, sentinel),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);

        Assert.Empty(plan.Invalidated);
        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        Assert.Equal(Newer, plan.NewWatermarks[SqlObjectType.StoredProcedure].Value);
    }

    [Fact]
    public void WhenTheSentinelIsGone_TheTypeMaximumDecides()
    {
        // No sentinel to compare, so the type's maximum is used instead. It cannot tell a restore
        // from the newest object simply being dropped, and errs toward the full scan -- one
        // unnecessary scan is the right way round to be wrong.
        var dropped = Item(SqlObjectType.View, "v_Newest", Watermark);
        var survivor = Item(SqlObjectType.View, "v_Old", Older);

        var plan = IncrementalPlanner.Plan(
            [survivor],
            Prior(dropped, survivor),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                [SqlObjectType.View] = Sentinelled(Watermark, dropped),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);

        Assert.Equal(WatermarkInvalidation.TimelineMovedBackwards, plan.Invalidated[SqlObjectType.View]);
    }

    [Fact]
    public void ATrustedWatermark_IsNeverLowered()
    {
        // Persistence is a blind upsert, so a lower value written here silently replaces a higher
        // one. A watermark may only move down when it has been invalidated -- which is exactly when
        // the type was scanned in full and the lower value is the truthful one.
        var sentinel = Item(SqlObjectType.Table, "T_Newest", Watermark);
        var remaining = Item(SqlObjectType.Table, "T_Old", Older);

        var plan = IncrementalPlanner.Plan(
            [remaining],
            Prior(sentinel, remaining),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                // The sentinel is recorded and gone from the snapshot, and the type's maximum is
                // BELOW the watermark, so this run is invalidated and re-baselined downward.
                [SqlObjectType.Table] = Sentinelled(Watermark, sentinel),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);
        Assert.Equal(Older, plan.NewWatermarks[SqlObjectType.Table].Value);

        // ...whereas a TRUSTED watermark keeps its value even when this run's snapshot happens to
        // contain nothing newer.
        var trusted = IncrementalPlanner.Plan(
            [remaining],
            Prior(sentinel, remaining),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                [SqlObjectType.Table] = Sentinelled(Older, remaining),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);
        Assert.Empty(trusted.Invalidated);
        Assert.Equal(Older, trusted.NewWatermarks[SqlObjectType.Table].Value);
    }

    [Fact]
    public void AChangedEmissionFingerprint_InvalidatesTheWatermark()
    {
        var item = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);

        var plan = IncrementalPlanner.Plan(
            [item],
            Prior(item),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                [SqlObjectType.StoredProcedure] = new(Watermark, "a-different-build"),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);

        Assert.Equal(WatermarkInvalidation.EmissionChanged, plan.Invalidated[SqlObjectType.StoredProcedure]);
        Assert.Empty(plan.SkippedItems);

        // The re-stamp carries the CURRENT fingerprint, so the next run is incremental again rather
        // than scanning in full forever.
        Assert.Equal(Fingerprint, plan.NewWatermarks[SqlObjectType.StoredProcedure].Fingerprint);
    }

    [Fact]
    public void ANullFingerprint_IsAdoptedRatherThanInvalidated()
    {
        // Rows written before fingerprints existed. Invalidating them would make the first run after
        // upgrading a full scan of every type, which on a large estate is hours.
        var item = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);

        var plan = IncrementalPlanner.Plan(
            [item],
            Prior(item),
            new Dictionary<SqlObjectType, ScriptingWatermark>
            {
                [SqlObjectType.StoredProcedure] = new(Watermark, Fingerprint: null),
            },
            IncrementalPlanner.CapableTypes,
            Fingerprint,
            NotIgnored);

        Assert.Empty(plan.Invalidated);
        Assert.Single(plan.SkippedItems);
    }

    [Fact]
    public void TheWatermarkRecordsWhichObjectSetIt()
    {
        var older = Item(SqlObjectType.View, "v_Old", Older);
        var newest = Item(SqlObjectType.View, "v_Newest", Newer);

        var plan = Plan([older, newest], Prior(older, newest), Watermarks(), NotIgnored);

        Assert.Equal(
            $"{(int)SqlObjectType.View}|dbo|v_Newest",
            plan.NewWatermarks[SqlObjectType.View].SentinelKey);
    }
}
