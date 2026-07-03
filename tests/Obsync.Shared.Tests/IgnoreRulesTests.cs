using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;
using Xunit;

namespace Obsync.Shared.Tests;

public sealed class IgnoreRulesTests
{
    [Fact]
    public void Parse_ReadsSections_CommentsAndBareLines()
    {
        const string content = """
            # ignore staging and temp objects
            schemas: staging, temp
            objects: dbo.tmp_*, *_bak
            types: view, proc
            dbo.zz_*            # bare line = object glob
            """;

        var rules = IgnoreRules.Parse(content);

        Assert.Equal(["staging", "temp"], rules.Schemas);
        Assert.Contains("dbo.tmp_*", rules.ObjectPatterns);
        Assert.Contains("*_bak", rules.ObjectPatterns);
        Assert.Contains("dbo.zz_*", rules.ObjectPatterns);          // bare line
        Assert.Contains(SqlObjectType.View, rules.Types);
        Assert.Contains(SqlObjectType.StoredProcedure, rules.Types); // "proc" alias
    }

    [Fact]
    public void Parse_SkipsUnknownTypeTokens_AndUnknownDirectives()
    {
        var rules = IgnoreRules.Parse("types: table, wibble\nnonsense: value");

        Assert.Contains(SqlObjectType.Table, rules.Types);
        Assert.Single(rules.Types);            // "wibble" skipped
        Assert.Empty(rules.ObjectPatterns);    // unknown "nonsense:" directive skipped, not a glob
    }

    [Theory]
    [InlineData("table", SqlObjectType.Table)]
    [InlineData("Tables", SqlObjectType.Table)]
    [InlineData("stored procedure", SqlObjectType.StoredProcedure)]
    [InlineData("StoredProcedure", SqlObjectType.StoredProcedure)]
    [InlineData("sproc", SqlObjectType.StoredProcedure)]
    [InlineData("view", SqlObjectType.View)]
    [InlineData("synonym", SqlObjectType.Synonym)]
    [InlineData("user", SqlObjectType.User)]
    public void TypeAliases_Resolve(string token, SqlObjectType expected)
    {
        Assert.True(SqlObjectTypeAliases.TryResolve(token, out var type));
        Assert.Equal(expected, type);
    }

    [Fact]
    public void Matches_BySchema_ByPattern_ByType()
    {
        var rules = IgnoreRules.Parse("""
            schemas: staging
            objects: dbo.tmp_*
            types: synonym
            """);

        Assert.True(rules.Matches(SqlObjectType.Table, "staging", "Orders"));     // schema rule
        Assert.True(rules.Matches(SqlObjectType.Table, "dbo", "tmp_import"));     // object glob (qualified)
        Assert.True(rules.Matches(SqlObjectType.Synonym, "dbo", "Anything"));     // type rule
        Assert.False(rules.Matches(SqlObjectType.Table, "dbo", "Customers"));     // kept
    }

    [Fact]
    public void Matches_BareName_AsWellAsQualified()
    {
        var rules = IgnoreRules.Parse("objects: *_bak");
        Assert.True(rules.Matches(SqlObjectType.Table, "dbo", "Orders_bak"));
    }

    [Fact]
    public void AddObjectPatterns_MergesJobPatterns()
    {
        var rules = IgnoreRules.Parse(string.Empty);
        rules.AddObjectPatterns(["*_old", "  ", "staging.*"]);

        Assert.Equal(["*_old", "staging.*"], rules.ObjectPatterns); // blank dropped
        Assert.True(rules.Matches(SqlObjectType.View, "dbo", "Report_old"));
    }

    [Fact]
    public void Parse_EmptyOrNull_IsEmpty()
    {
        Assert.True(IgnoreRules.Parse(null).IsEmpty);
        Assert.True(IgnoreRules.Parse("   \n # only a comment \n").IsEmpty);
    }

    [Theory]
    [InlineData("Orders", "Orders", true)]
    [InlineData("orders", "Orders", true)]      // case-insensitive
    [InlineData("dbo.tmp_x", "dbo.tmp_*", true)]
    [InlineData("dbo.tmp", "dbo.tmp?", false)]  // ? requires exactly one more char
    [InlineData("dbo.tmp1", "dbo.tmp?", true)]
    public void Glob_IsMatch(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(text, pattern));
    }
}
