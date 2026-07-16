using System.Globalization;
using Obsync.Metadata;

namespace Obsync.Metadata.Tests;

public sealed class ReferenceDataScriptBuilderTests
{
    private static readonly ReferenceDataColumn Id = new("Id", "int", IsIdentity: false);
    private static readonly ReferenceDataColumn NameCol = new("Name", "nvarchar", IsIdentity: false);

    [Fact]
    public void Build_EmitsColumnList_PkOrderNote_AndValues()
    {
        var script = ReferenceDataScriptBuilder.Build(
            "dbo", "Currency", [Id, NameCol],
            [[1, "Euro"], [2, "US 'Dollar'"]],
            ["Id"]);

        Assert.Contains("-- Reference data for [dbo].[Currency] — 2 row(s).", script);
        Assert.Contains("-- Ordered by Id for stable diffs.", script);
        Assert.Contains("INSERT INTO [dbo].[Currency] ([Id], [Name])", script);
        Assert.Contains("    (1, N'Euro'),", script);
        Assert.Contains("    (2, N'US ''Dollar''');", script); // quotes doubled, last row terminates
        Assert.DoesNotContain("IDENTITY_INSERT", script);
    }

    [Fact]
    public void Build_WrapsIdentityColumns_WithIdentityInsert()
    {
        var identityId = new ReferenceDataColumn("Id", "int", IsIdentity: true);
        var script = ReferenceDataScriptBuilder.Build("ref", "Status", [identityId, NameCol], [[1, "Open"]], ["Id"]);

        Assert.Contains("SET IDENTITY_INSERT [ref].[Status] ON;", script);
        Assert.Contains("SET IDENTITY_INSERT [ref].[Status] OFF;", script);
        // ON comes before the INSERT, OFF after it.
        Assert.True(script.IndexOf("IDENTITY_INSERT [ref].[Status] ON", StringComparison.Ordinal)
            < script.IndexOf("INSERT INTO", StringComparison.Ordinal));
        Assert.True(script.IndexOf("INSERT INTO", StringComparison.Ordinal)
            < script.IndexOf("IDENTITY_INSERT [ref].[Status] OFF", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_EmptyTable_EmitsHeaderOnly()
    {
        var script = ReferenceDataScriptBuilder.Build("dbo", "Empty", [Id], [], ["Id"]);

        Assert.Contains("0 row(s)", script);
        Assert.Contains("-- (no rows)", script);
        Assert.DoesNotContain("INSERT INTO", script);
    }

    [Fact]
    public void Build_BatchesEveryThousandRows_IntoSeparateInserts()
    {
        var rows = Enumerable.Range(1, 1001).Select(i => (object?[])[i]).ToList();
        var script = ReferenceDataScriptBuilder.Build("dbo", "Big", [Id], rows, ["Id"]);

        var inserts = script.Split("INSERT INTO").Length - 1;
        Assert.Equal(2, inserts); // 1000 + 1
        Assert.Contains("    (1000);", script);   // first batch terminator
        Assert.Contains("    (1001);", script);   // second batch terminator
    }

    [Fact]
    public void Build_QuotesBracketsInIdentifiers()
    {
        var odd = new ReferenceDataColumn("We]ird", "int", IsIdentity: false);
        var script = ReferenceDataScriptBuilder.Build("dbo", "A]B", [odd], [[1]], ["We]ird"]);

        Assert.Contains("[A]]B]", script);
        Assert.Contains("[We]]ird]", script);
    }

    [Fact]
    public void FormatLiteral_IsInvariantAndTypeAware()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma culture
        try
        {
            Assert.Equal("NULL", ReferenceDataScriptBuilder.FormatLiteral(null));
            Assert.Equal("NULL", ReferenceDataScriptBuilder.FormatLiteral(DBNull.Value));
            Assert.Equal("N'it''s'", ReferenceDataScriptBuilder.FormatLiteral("it's"));
            Assert.Equal("1", ReferenceDataScriptBuilder.FormatLiteral(true));
            Assert.Equal("0", ReferenceDataScriptBuilder.FormatLiteral(false));
            Assert.Equal("3.14", ReferenceDataScriptBuilder.FormatLiteral(3.14m));
            Assert.Equal("0.5", ReferenceDataScriptBuilder.FormatLiteral(0.5));
            Assert.Equal("42", ReferenceDataScriptBuilder.FormatLiteral(42));
            Assert.Equal("0xDEADBEEF", ReferenceDataScriptBuilder.FormatLiteral(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
            Assert.Equal("0x", ReferenceDataScriptBuilder.FormatLiteral(Array.Empty<byte>()));
            Assert.Equal("'d55e46ea-53b0-4f4b-a5d5-4a5f04d16dcd'",
                ReferenceDataScriptBuilder.FormatLiteral(Guid.Parse("d55e46ea-53b0-4f4b-a5d5-4a5f04d16dcd")));
            Assert.Equal("'2026-07-05T13:30:15.5000000'",
                ReferenceDataScriptBuilder.FormatLiteral(new DateTime(2026, 7, 5, 13, 30, 15, 500)));
            Assert.Equal("'13:30:15'", ReferenceDataScriptBuilder.FormatLiteral(new TimeSpan(13, 30, 15)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Build_IsDeterministic_ForIdenticalInput()
    {
        object?[][] rows = [[1, "A"], [2, "B"]];
        var first = ReferenceDataScriptBuilder.Build("dbo", "T", [Id, NameCol], rows, ["Id"]);
        var second = ReferenceDataScriptBuilder.Build("dbo", "T", [Id, NameCol], rows, ["Id"]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FormatLiteral_NewlinesInData_NeverSpanPhysicalLines()
    {
        // A raw newline inside the literal would be rewritten by the engine's line-based script
        // normalization (CRLF→LF + trailing-space trim), silently corrupting the exported data.
        var literal = ReferenceDataScriptBuilder.FormatLiteral("line1\r\nline2");

        Assert.Equal("(N'line1' + CHAR(13) + CHAR(10) + N'line2')", literal);
        Assert.DoesNotContain('\n', literal);
        Assert.DoesNotContain('\r', literal);
    }

    [Fact]
    public void FormatLiteral_NewlineEdgeCases_AreExact()
    {
        Assert.Equal("(CHAR(10))", ReferenceDataScriptBuilder.FormatLiteral("\n"));
        Assert.Equal("(N'a' + CHAR(13))", ReferenceDataScriptBuilder.FormatLiteral("a\r"));
        Assert.Equal("(CHAR(10) + N'it''s')", ReferenceDataScriptBuilder.FormatLiteral("\nit's"));
    }
}
