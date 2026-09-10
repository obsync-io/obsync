using System.Text;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_IsByteIdenticalToSerialize(bool empty)
    {
        // The engine hashes and writes the STREAMED form; a single differing byte would make every
        // deployed installation re-commit its inventory (and mismatch all stored hashes). Serialize
        // stays the reference implementation this locks against. Unicode entry included: multi-byte
        // UTF-8 is where a divergent encoder would show.
        ObjectInventoryEntry[] entries = empty
            ? []
            : [.. Entries, new("Table", "vault", "Ünïcødé ✓", "tables/vault.Ünïcødé ✓.sql", "dddd")];

        var reference = System.Text.Encoding.UTF8.GetBytes(
            ObjectInventoryWriter.Serialize("SRV", "SalesDB", entries));

        using var streamed = new MemoryStream();
        await ObjectInventoryWriter.WriteAsync(streamed, "SRV", "SalesDB", entries);

        Assert.Equal(reference, streamed.ToArray());
    }
    /// <summary>
    /// The size prediction must be measured against the REAL writer, not against an idea of it.
    /// Past roughly 380,000 objects the manifest clears the size guard, and the prediction is what
    /// stops it being generated and discarded on every run — so if the two drift apart, the product
    /// either resumes doing the pointless work or starts refusing manifests it could have written.
    /// </summary>
    [Fact]
    public void TheSizeEstimate_MatchesWhatTheWriterActuallyProduces()
    {
        const int count = 5_000;
        var entries = Enumerable.Range(0, count).Select(i => new ObjectInventoryEntry(
            "StoredProcedure",
            "dbo",
            $"usp_SomeReasonablyTypicalProcedureName_{i:D6}",
            $"db/procedures/dbo.usp_SomeReasonablyTypicalProcedureName_{i:D6}.sql",
            new string('a', 64))).ToList();

        var actualBytesPerEntry =
            Encoding.UTF8.GetByteCount(ObjectInventoryWriter.Serialize("server", "db", entries)) / (double)count;

        // Wide, because it is an estimate used with a wide margin -- but tight enough that a change
        // to the manifest's shape cannot pass unnoticed.
        var ratio = ObjectInventoryWriter.ApproximateBytesPerEntry / actualBytesPerEntry;
        Assert.True(
            ratio is > 0.6 and < 1.6,
            $"The writer produces {actualBytesPerEntry:N0} bytes per entry, but "
            + $"ApproximateBytesPerEntry says {ObjectInventoryWriter.ApproximateBytesPerEntry}. Update the "
            + "constant: the engine uses it to decide whether generating the manifest is worth attempting "
            + "at all.");
    }
}
