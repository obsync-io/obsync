using Obsync.Metadata;

namespace Obsync.Metadata.Tests;

/// <summary>Friendly labels for catalog type_desc values in dependency results.</summary>
public sealed class DependencyTypeLabelTests
{
    [Theory]
    [InlineData("USER_TABLE", "Table")]
    [InlineData("VIEW", "View")]
    [InlineData("SQL_STORED_PROCEDURE", "Stored procedure")]
    [InlineData("SQL_INLINE_TABLE_VALUED_FUNCTION", "Function")]
    [InlineData("SQL_TRIGGER", "Trigger")]
    [InlineData("SYNONYM", "Synonym")]
    [InlineData("SEQUENCE_OBJECT", "Sequence")]
    [InlineData("OBSYNC_FK_TABLE", "Table (foreign key)")]
    [InlineData("OBSYNC_CROSS_DB", "Cross-database reference")]
    [InlineData("OBSYNC_UNRESOLVED", "Unresolved reference")]
    public void KnownTypes_MapToFriendlyLabels(string typeDesc, string expected) =>
        Assert.Equal(expected, SqlServerProbe.DependencyTypeLabel(typeDesc));

    [Fact]
    public void UnknownTypes_AreHumanized_NotThrown() =>
        Assert.Equal("Service queue", SqlServerProbe.DependencyTypeLabel("SERVICE_QUEUE"));
}
