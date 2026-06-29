using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

public sealed class ScriptNormalizerTests
{
    private readonly ScriptNormalizer _normalizer = new();

    [Fact]
    public void Normalize_ConvertsCrlfAndCrToLf()
    {
        var result = _normalizer.Normalize("a\r\nb\rc");

        Assert.Equal("a\nb\nc\n", result);
    }

    [Fact]
    public void Normalize_TrimsTrailingWhitespacePerLine()
    {
        var result = _normalizer.Normalize("SELECT 1   \n   FROM t\t\n");

        Assert.Equal("SELECT 1\n   FROM t\n", result);
    }

    [Fact]
    public void Normalize_StripsObjectHeaderWithScriptDate()
    {
        const string input =
            "/****** Object:  StoredProcedure [dbo].[x]    Script Date: 6/28/2026 11:00:00 PM ******/\n" +
            "CREATE PROCEDURE dbo.x AS SELECT 1;\n";

        var result = _normalizer.Normalize(input);

        Assert.Equal("CREATE PROCEDURE dbo.x AS SELECT 1;\n", result);
    }

    [Fact]
    public void Normalize_HeaderStrippingMakesHashStableAcrossTimestamps()
    {
        var first = _normalizer.Normalize(
            "/****** Object:  View [dbo].[v]    Script Date: 1/1/2026 1:00:00 AM ******/\nCREATE VIEW dbo.v AS SELECT 1 AS a;\n");
        var second = _normalizer.Normalize(
            "/****** Object:  View [dbo].[v]    Script Date: 9/9/2026 9:09:09 PM ******/\nCREATE VIEW dbo.v AS SELECT 1 AS a;\n");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Normalize_CollapsesTrailingBlankLinesToSingleNewline()
    {
        var result = _normalizer.Normalize("CREATE VIEW dbo.v AS SELECT 1;\n\n\n\n");

        Assert.Equal("CREATE VIEW dbo.v AS SELECT 1;\n", result);
    }

    [Fact]
    public void Normalize_CanEmitCrlfWhenLineEndingNormalizationDisabled()
    {
        var options = new NormalizationOptions { NormalizeLineEndings = false };

        var result = _normalizer.Normalize("a\nb\n", options);

        Assert.Equal("a\r\nb\r\n", result);
    }

    [Fact]
    public void Normalize_EmptyInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, _normalizer.Normalize(string.Empty));
    }
}
