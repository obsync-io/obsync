using Obsync.Engine;
using Obsync.Shared.Scripting;

namespace Obsync.Engine.Tests;

public sealed class ReferenceDataTests
{
    [Theory]
    [InlineData("dbo.Currency", "dbo", "Currency")]
    [InlineData("  ref.Status  ", "ref", "Status")]
    [InlineData("Country", "dbo", "Country")] // bare name defaults to dbo
    [InlineData("dbo.Weird.Name", "dbo", "Weird.Name")] // split on the FIRST dot only
    public void SplitTableName_ParsesSchemaAndTable(string entry, string schema, string table)
    {
        Assert.Equal((schema, table), SyncEngine.SplitTableName(entry));
    }

    [Fact]
    public void ReferenceDataFile_BuildsDataPath_AndSanitizesInvalidChars()
    {
        Assert.Equal("data/dbo.Currency.sql", RepositoryLayout.ReferenceDataFile("dbo", "Currency"));
        Assert.Equal("data/dbo.Bad_Name.sql", RepositoryLayout.ReferenceDataFile("dbo", "Bad|Name"));
    }
}
