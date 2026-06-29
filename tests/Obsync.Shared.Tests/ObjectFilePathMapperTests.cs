using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

public sealed class ObjectFilePathMapperTests
{
    private readonly ObjectFilePathMapper _mapper = new();

    [Theory]
    [InlineData(SqlObjectType.StoredProcedure, "dbo", "usp_GetCustomer", "procedures/dbo.usp_GetCustomer.sql")]
    [InlineData(SqlObjectType.View, "dbo", "vw_SalesSummary", "views/dbo.vw_SalesSummary.sql")]
    [InlineData(SqlObjectType.Function, "dbo", "fn_OldTax", "functions/dbo.fn_OldTax.sql")]
    [InlineData(SqlObjectType.Table, "sales", "Orders", "tables/sales.Orders.sql")]
    public void MapRelativePath_SchemaScopedTypes_UseSchemaPrefixedFileName(
        SqlObjectType type, string schema, string name, string expected)
    {
        var path = _mapper.MapRelativePath(new ScriptedObjectIdentity(type, schema, name));

        Assert.Equal(expected, path);
    }

    [Fact]
    public void MapRelativePath_NonSchemaScopedType_OmitsSchemaPrefix()
    {
        var path = _mapper.MapRelativePath(new ScriptedObjectIdentity(SqlObjectType.User, "", "AppLogin"));

        Assert.Equal("security/users/AppLogin.sql", path);
    }

    [Fact]
    public void MapRelativePath_UsesLowercaseGitFriendlyFolders()
    {
        var path = _mapper.MapRelativePath(new ScriptedObjectIdentity(SqlObjectType.StoredProcedure, "dbo", "x"));

        Assert.StartsWith("procedures/", path, StringComparison.Ordinal);
    }

    [Fact]
    public void MapRelativePath_SanitizesInvalidCharactersAndStaysDeterministic()
    {
        var identity = new ScriptedObjectIdentity(SqlObjectType.StoredProcedure, "dbo", "weird:name?");

        var first = _mapper.MapRelativePath(identity);
        var second = _mapper.MapRelativePath(identity);

        Assert.Equal(first, second);
        Assert.DoesNotContain(':', first);
        Assert.DoesNotContain('?', first);
        Assert.EndsWith(".sql", first, StringComparison.Ordinal);
    }

    [Fact]
    public void MapRelativePath_TwoNamesThatSanitizeIdentically_DoNotCollide()
    {
        // "a:b" and "a?b" both sanitize to "a_b" but must map to distinct files.
        var first = _mapper.MapRelativePath(new ScriptedObjectIdentity(SqlObjectType.Table, "dbo", "a:b"));
        var second = _mapper.MapRelativePath(new ScriptedObjectIdentity(SqlObjectType.Table, "dbo", "a?b"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MapRelativePath_VeryLongName_IsTruncatedButStillSqlFile()
    {
        var longName = new string('x', 400);

        var path = _mapper.MapRelativePath(new ScriptedObjectIdentity(SqlObjectType.Table, "dbo", longName));

        var fileName = path.Split('/')[^1];
        Assert.True(fileName.Length <= 160);
        Assert.EndsWith(".sql", fileName, StringComparison.Ordinal);
    }
}
