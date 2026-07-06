using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Shared.Tests;

public sealed class ObjectSelectionTests
{
    [Fact]
    public void ProgrammabilityOnly_ContainsModulesAndNoTables()
    {
        var types = ObjectSelectionPresets.Expand(ObjectSelectionPreset.ProgrammabilityOnly);

        Assert.Contains(SqlObjectType.StoredProcedure, types);
        Assert.Contains(SqlObjectType.View, types);
        Assert.Contains(SqlObjectType.Function, types);
        Assert.Contains(SqlObjectType.Trigger, types);
        Assert.DoesNotContain(SqlObjectType.Table, types);
    }

    [Fact]
    public void FullSchema_CoversEveryCatalogedType()
    {
        var types = ObjectSelectionPresets.Expand(ObjectSelectionPreset.FullSchema);

        Assert.Equal(SqlObjectTypeCatalog.All.Count, types.Count);
    }

    [Fact]
    public void Custom_ResolvesToExplicitTypesInRedeployOrder()
    {
        var profile = new ObjectSelectionProfile
        {
            Preset = ObjectSelectionPreset.Custom,
            CustomTypes = [SqlObjectType.Trigger, SqlObjectType.Schema, SqlObjectType.Table],
        };

        var resolved = profile.ResolveTypes();

        // Schema (order 4) before Table (42) before Trigger (46).
        Assert.Equal([SqlObjectType.Schema, SqlObjectType.Table, SqlObjectType.Trigger], resolved);
    }

    [Fact]
    public void InRedeployOrder_PlacesPrincipalsBeforeTablesBeforePolicies()
    {
        var ordered = SqlObjectTypeCatalog.InRedeployOrder(
        [
            SqlObjectType.SecurityPolicy,
            SqlObjectType.Table,
            SqlObjectType.User,
        ]);

        Assert.Equal([SqlObjectType.User, SqlObjectType.Table, SqlObjectType.SecurityPolicy], ordered);
    }

    [Fact]
    public void Catalog_EveryScriptableTypeHasADescriptor()
    {
        // Every user-selectable, provider-routed type must have a catalog descriptor. The synthetic
        // DatabaseArtifact/ReferenceData types are deliberately excluded — the engine generates
        // those files directly with fixed paths and never routes them through the catalog or the
        // path mapper.
        foreach (var type in Enum.GetValues<SqlObjectType>())
        {
            if (type is SqlObjectType.DatabaseArtifact or SqlObjectType.ReferenceData)
            {
                continue;
            }

            Assert.True(SqlObjectTypeCatalog.TryGet(type, out _), $"Missing descriptor for {type}");
        }
    }

    [Theory]
    [InlineData(SqlObjectType.DatabaseArtifact)]
    [InlineData(SqlObjectType.ReferenceData)]
    public void Catalog_SyntheticTypesAreNotCataloged(SqlObjectType type)
    {
        Assert.False(SqlObjectTypeCatalog.TryGet(type, out _));
    }
}
