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

    private static Dictionary<string, TrackedObjectState> Prior(params ModifiedObjectSnapshotItem[] items) =>
        items.ToDictionary(
            i => $"{(int)i.Type}|{i.Schema}|{i.Name}",
            i => new TrackedObjectState
            {
                ObjectType = i.Type,
                SchemaName = i.Schema,
                ObjectName = i.Name,
                FilePath = $"env/db/x/{i.Schema}.{i.Name}.sql",
                LastHash = $"hash-{i.Name}",
            },
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<SqlObjectType, DateTime> Watermarks(params SqlObjectType[] types) =>
        types.ToDictionary(t => t, _ => Watermark);

    private static bool NotIgnored(SqlObjectType type, string schema, string name) => false;

    [Fact]
    public void Skips_OlderObject_WithWatermarkAndPriorState()
    {
        var item = Item(SqlObjectType.StoredProcedure, "usp_Old", Older);

        var plan = IncrementalPlanner.Plan([item], Prior(item), Watermarks(SqlObjectType.StoredProcedure), NotIgnored);

        var skip = Assert.Single(plan.SkippedItems);
        Assert.Equal(item, skip.Item);
        Assert.Equal("hash-usp_Old", skip.PriorState.LastHash);
        Assert.Contains(SqlObjectType.StoredProcedure, plan.FilterableTypes);
    }

    [Fact]
    public void DoesNotSkip_WhenTheTypeHasNoWatermark()
    {
        var item = Item(SqlObjectType.View, "vw_Old", Older);

        var plan = IncrementalPlanner.Plan([item], Prior(item), Watermarks(), NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Empty(plan.FilterableTypes); // no watermark → nothing filterable either
    }

    [Fact]
    public void DoesNotSkip_ObjectModifiedOnTheWatermarkBoundary()
    {
        // modify_date == watermark must be re-read: the provider filter is >=, closing the
        // race where an object changes within the snapshot's last 3.33ms tick.
        var item = Item(SqlObjectType.View, "vw_Boundary", Watermark);

        var plan = IncrementalPlanner.Plan([item], Prior(item), Watermarks(SqlObjectType.View), NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Contains(SqlObjectType.View, plan.FilterableTypes); // boundary is no violation
    }

    [Fact]
    public void DoesNotSkip_ModifiedObject()
    {
        var item = Item(SqlObjectType.Table, "Orders", Newer);

        var plan = IncrementalPlanner.Plan([item], Prior(item), Watermarks(SqlObjectType.Table), NotIgnored);

        Assert.Empty(plan.SkippedItems);
        Assert.Contains(SqlObjectType.Table, plan.FilterableTypes);
    }

    [Fact]
    public void DoesNotSkip_IgnoredObject_AndIgnoredObjectIsNoViolation()
    {
        // An old object with no prior state would be a violation — unless it is ignored, in
        // which case the providers never yield it anyway.
        var ignored = Item(SqlObjectType.Table, "Ignored", Older);

        var plan = IncrementalPlanner.Plan(
            [ignored],
            new Dictionary<string, TrackedObjectState>(StringComparer.OrdinalIgnoreCase),
            Watermarks(SqlObjectType.Table),
            (_, _, name) => name == "Ignored");

        Assert.Empty(plan.SkippedItems);
        Assert.Contains(SqlObjectType.Table, plan.FilterableTypes);
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

        var plan = IncrementalPlanner.Plan(
            [skippable, violation, otherType],
            Prior(skippable, otherType),
            Watermarks(SqlObjectType.StoredProcedure, SqlObjectType.View),
            NotIgnored);

        Assert.DoesNotContain(SqlObjectType.StoredProcedure, plan.FilterableTypes);
        Assert.Contains(SqlObjectType.View, plan.FilterableTypes);
        var skip = Assert.Single(plan.SkippedItems); // the violated type's candidate is dropped too
        Assert.Equal(otherType, skip.Item);
    }

    [Fact]
    public void NewWatermarks_AreThePerTypeMax_AndAbsentForTypesWithoutSnapshotRows()
    {
        var plan = IncrementalPlanner.Plan(
            [
                Item(SqlObjectType.Table, "A", Older),
                Item(SqlObjectType.Table, "B", Newer),
                Item(SqlObjectType.View, "C", Watermark),
            ],
            new Dictionary<string, TrackedObjectState>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<SqlObjectType, DateTime>(),
            NotIgnored);

        Assert.Equal(Newer, plan.NewWatermarks[SqlObjectType.Table]);
        Assert.Equal(Watermark, plan.NewWatermarks[SqlObjectType.View]);
        Assert.False(plan.NewWatermarks.ContainsKey(SqlObjectType.StoredProcedure));
    }

    [Fact]
    public void EmptySnapshot_KeepsWatermarkedTypesFilterable_AndProducesNoNewWatermarks()
    {
        var plan = IncrementalPlanner.Plan(
            [],
            new Dictionary<string, TrackedObjectState>(StringComparer.OrdinalIgnoreCase),
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
}
