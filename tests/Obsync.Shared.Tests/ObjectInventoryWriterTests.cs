using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

public sealed class ObjectInventoryWriterTests
{
    private static readonly ObjectInventoryEntry[] Entries =
    [
        new("StoredProcedure", "dbo", "usp_GetCustomer", "procedures/dbo.usp_GetCustomer.sql", "aaaa"),
        new("View", "dbo", "vw_Sales", "views/dbo.vw_Sales.sql", "bbbb"),
        new("StoredProcedure", "dbo", "usp_AddOrder", "procedures/dbo.usp_AddOrder.sql", "cccc"),
    ];

    [Fact]
    public void Serialize_IsIndependentOfInputOrder()
    {
        var forward = ObjectInventoryWriter.Serialize("SRV", "SalesDB", Entries);
        var reversed = ObjectInventoryWriter.Serialize("SRV", "SalesDB", Entries.Reverse());

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Serialize_UsesLfEndingsAndSingleTrailingNewline()
    {
        var json = ObjectInventoryWriter.Serialize("SRV", "SalesDB", Entries);

        Assert.DoesNotContain('\r', json);
        Assert.EndsWith("}\n", json);
        Assert.DoesNotContain("}\n\n", json);
    }

    [Fact]
    public void Serialize_RecordsCountAndPerTypeBreakdown()
    {
        var json = ObjectInventoryWriter.Serialize("SRV", "SalesDB", Entries);

        Assert.Contains("\"ObjectCount\": 3", json);
        Assert.Contains("\"StoredProcedure\": 2", json);
        Assert.Contains("\"View\": 1", json);
        Assert.Contains("\"Server\": \"SRV\"", json);
        Assert.Contains("\"Database\": \"SalesDB\"", json);
    }

    [Fact]
    public void Serialize_ChangesWhenAnObjectHashChanges()
    {
        var before = ObjectInventoryWriter.Serialize("SRV", "SalesDB", Entries);
        var mutated = Entries.ToArray();
        mutated[0] = mutated[0] with { Hash = "zzzz" };

        var after = ObjectInventoryWriter.Serialize("SRV", "SalesDB", mutated);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Serialize_EmptyInventory_ProducesZeroCount()
    {
        var json = ObjectInventoryWriter.Serialize("SRV", "SalesDB", []);

        Assert.Contains("\"ObjectCount\": 0", json);
        Assert.Contains("\"Objects\": []", json);
    }
}
