using Obsync.Metadata;

namespace Obsync.Metadata.Tests;

/// <summary>SSMS-style data-type display strings in the generated data dictionary.</summary>
public sealed class DocumentationDataTypeTests
{
    [Theory]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")] // max_length is bytes; nvarchar chars are half
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 25, 0, 0, "varchar(25)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("time", 5, 16, 7, "time(7)")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    public void FormatDataType_MatchesSsmsDisplay(string type, short maxLength, byte precision, byte scale, string expected) =>
        Assert.Equal(expected, DatabaseDocumentationReader.FormatDataType(type, maxLength, precision, scale));
}
