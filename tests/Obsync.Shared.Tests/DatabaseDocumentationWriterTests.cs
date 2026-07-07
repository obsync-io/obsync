using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

/// <summary>The generated docs/README.md: deterministic markdown, correct counts, safe cells.</summary>
public sealed class DatabaseDocumentationWriterTests
{
    private static readonly ObjectInventoryEntry[] Inventory =
    [
        new("Table", "dbo", "Customers", "tables/dbo.Customers.sql", "h1"),
        new("Table", "dbo", "Orders", "tables/dbo.Orders.sql", "h2"),
        new("View", "reporting", "vw_Sales", "views/reporting.vw_Sales.sql", "h3"),
        new("StoredProcedure", "dbo", "usp_GetOrder", "procedures/dbo.usp_GetOrder.sql", "h4"),
    ];

    private static DatabaseDocumentationData Data(int total = 1) => new(
        [
            new DocumentationTable("dbo", "Customers", "Master customer list | source of truth.",
            [
                new DocumentationColumn("Id", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: true,
                    IsComputed: false, Default: null, Description: null),
                new DocumentationColumn("Name", "nvarchar(50)", IsNullable: false, IsPrimaryKey: false,
                    IsIdentity: false, IsComputed: false, Default: "('')", Description: "Display\nname"),
            ]),
        ], total);

    [Fact]
    public void Build_ContainsHeaderOverviewAndDictionary_WithLfNewlinesOnly()
    {
        var markdown = DatabaseDocumentationWriter.Build("SQL01", "SalesDB", Inventory, Data());

        Assert.StartsWith("# SalesDB", markdown);
        Assert.DoesNotContain("\r", markdown);
        Assert.Contains("4 objects are tracked.", markdown);
        Assert.Contains("| Table | 2 |", markdown);
        Assert.Contains("| Stored procedure | 1 |", markdown); // humanized type name
        Assert.Contains("| `dbo` | 3 |", markdown);
        Assert.Contains("### `dbo.Customers`", markdown);
        Assert.Contains("| `Id` | `int` identity | no | — | PK |  |", markdown);
    }

    [Fact]
    public void Build_EscapesPipesAndNewlines_InsideTableCells()
    {
        var markdown = DatabaseDocumentationWriter.Build("SQL01", "SalesDB", Inventory, Data());

        // The table description contains '|' and the column description a newline — both must stay
        // inside their markdown cells.
        Assert.Contains(@"Master customer list \| source of truth.", markdown);
        Assert.Contains("Display name", markdown);
    }

    [Fact]
    public void Build_NotesTruncation_OnlyWhenTablesWereCapped()
    {
        Assert.DoesNotContain("lists the first", DatabaseDocumentationWriter.Build("s", "d", Inventory, Data(total: 1)));
        Assert.Contains(
            "The dictionary lists the first 1 of 3,000 tables.",
            DatabaseDocumentationWriter.Build("s", "d", Inventory, Data(total: 3000)));
    }

    [Fact]
    public void Build_HandlesAnEmptyDatabase()
    {
        var markdown = DatabaseDocumentationWriter.Build("SQL01", "Empty", [], new DatabaseDocumentationData([], 0));

        Assert.Contains("0 objects are tracked.", markdown);
        Assert.Contains("_No tables to document._", markdown);
    }

    [Theory]
    [InlineData("StoredProcedure", "Stored procedure")]
    [InlineData("Table", "Table")]
    [InlineData("XmlSchemaCollection", "Xml schema collection")]
    public void Humanize_SplitsPascalCase(string input, string expected) =>
        Assert.Equal(expected, DatabaseDocumentationWriter.Humanize(input));
}
