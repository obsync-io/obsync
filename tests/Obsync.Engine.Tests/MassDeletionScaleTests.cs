using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Xunit;

namespace Obsync.Engine.Tests;

/// <summary>
/// The mass-deletion circuit breaker at estate scale.
/// </summary>
/// <remarks>
/// The large-absolute-loss rule used a flat count of 100, which is not a scale-free signal. On the
/// VLDBs this product targets, losing more than a hundred tracked objects in a day is ordinary — one
/// release retiring a staging schema does it — so the rule fired on essentially every scheduled run.
/// The effect was the opposite of caution: deletions were suspended daily, files of dropped objects
/// were never removed, and the remedy the warning offers ("use Run Now in the app") does not exist on
/// a headless service install. A guard that always fires protects nothing.
/// </remarks>
public sealed class MassDeletionScaleTests
{
    private static TrackedObjectSnapshot Obj(int i, string schema = "dbo") => new()
    {
        Id = i,
        ObjectType = SqlObjectType.StoredProcedure,
        SchemaName = schema,
        ObjectName = $"usp_{i:D7}",
        FilePath = $"db/procedures/{schema}.usp_{i:D7}.sql",
        LastHash = "hash",
    };

    private static List<TrackedObjectSnapshot> Estate(int count, string schema = "dbo") =>
        [.. Enumerable.Range(0, count).Select(i => Obj(i, schema))];

    [Fact]
    public void AnOrdinaryDropOnALargeEstate_IsNotTreatedAsLostVisibility()
    {
        // 500 of a million is 0.05% — a normal release. Under the flat rule this suspended
        // deletions and escalated the run to Warning, every single day, for ever.
        var inScope = Estate(1_000_000);
        var candidates = inScope.Take(500).ToList();

        Assert.Null(SyncEngine.DisappearanceLooksLikeLostVisibility(candidates, inScope));
    }

    [Fact]
    public void ALargeProportionalLossOnALargeEstate_IsStillCaught()
    {
        // 5% of a million. The rule still exists; it just scales.
        var inScope = Estate(1_000_000);
        var candidates = inScope.Take(50_000).ToList();

        Assert.NotNull(SyncEngine.DisappearanceLooksLikeLostVisibility(candidates, inScope));
    }

    [Fact]
    public void OnASmallEstate_TheAbsoluteFloorStillApplies()
    {
        // 100 of 1,000 is 10% — below the proportional share, so the floor is what catches it.
        // Small scopes must not become MORE permissive than they were.
        var inScope = Estate(1_000);
        var candidates = inScope.Take(100).ToList();

        Assert.NotNull(SyncEngine.DisappearanceLooksLikeLostVisibility(candidates, inScope));
    }

    [Fact]
    public void AWholeSchemaWipeOnALargeEstate_IsStillCaught()
    {
        // The signature of a schema-scoped DENY, and the case the proportional rule alone would
        // miss: 40 objects gone out of a million is nothing proportionally, but it is every object
        // of that schema.
        var inScope = Estate(1_000_000);
        inScope.AddRange(Enumerable.Range(0, 40).Select(i => Obj(2_000_000 + i, "reporting")));
        var candidates = inScope.Where(o => o.SchemaName == "reporting").ToList();

        var reason = SyncEngine.DisappearanceLooksLikeLostVisibility(candidates, inScope);
        Assert.Contains("reporting", reason);
    }

    [Fact]
    public void ATotalWipe_IsCaughtAtAnySize()
    {
        var inScope = Estate(3);
        Assert.NotNull(SyncEngine.DisappearanceLooksLikeLostVisibility(inScope, inScope));
    }
}
