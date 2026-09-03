using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

/// <summary>
/// Sanitizing a server-supplied name into exactly one path component. The database name goes
/// through this: unsanitized it was composed straight into the repository path, so a name carrying
/// separators or <c>..</c> redirected every write for that database outside the workspace.
/// </summary>
public sealed class PathSegmentSanitizerTests
{
    private static string Segment(string name) => ObjectFilePathMapper.SanitizePathSegment(name);

    [Theory]
    [InlineData("SalesDB")]
    [InlineData("Sales_2026")]
    [InlineData("Sales-DB")]
    public void OrdinaryNamesArePassedThroughUnchanged(string name) =>
        // Existing repositories must not be relocated by this guard.
        Assert.Equal(name, Segment(name));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData(@"..\..\Windows\Temp")]
    [InlineData("../../etc")]
    [InlineData(@"C:\Windows")]
    [InlineData("C:")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("")]
    [InlineData("   ")]
    public void HostileNamesCollapseToASingleComponent(string name)
    {
        var segment = Segment(name);

        Assert.DoesNotContain('/', segment);
        Assert.DoesNotContain('\\', segment);
        Assert.DoesNotContain(':', segment);
        // Not a traversal component, and not empty.
        Assert.NotEqual("..", segment);
        Assert.NotEqual(".", segment);
        Assert.NotEmpty(segment);
    }

    [Fact]
    public void NamesThatSanitizeAlikeStayDistinct()
    {
        // Without the stable suffix these would all collapse onto one folder and silently merge
        // several databases into a single tree.
        string[] hostile = ["..", ".", "...", @"a\b", "a/b", ""];

        var segments = hostile.Select(Segment).ToList();

        Assert.Equal(segments.Count, segments.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TrailingDotsAndSpacesDoNotMergeDatabases() =>
        // Windows silently strips them, so "Sales." and "Sales" would otherwise be one folder.
        Assert.Equal(3, new[] { "Sales", "Sales.", "Sales " }.Select(Segment)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("AUX")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("CON.foo")]
    [InlineData("con ")]
    public void ReservedDeviceNamesAreMangled(string name)
    {
        // A directory named CON makes `git add` skip it with a warning and exit 0 — which the
        // engine reads as "no changes" — and a file named CON.sql makes `git add` fail outright.
        var segment = Segment(name);

        Assert.NotEqual(name, segment);
        Assert.StartsWith("_", segment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONX")]
    [InlineData("CON1")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("MyCon")]
    [InlineData("LPT0")]
    public void NamesThatMerelyResembleDeviceNamesAreLeftAlone(string name) =>
        Assert.Equal(name, Segment(name));

    [Fact]
    public void LongNamesAreTruncatedButStayDistinct()
    {
        var shared = new string('x', 200);

        Assert.NotEqual(Segment(shared + "A"), Segment(shared + "B"));
    }

    [Fact]
    public void SanitizingIsDeterministic() =>
        Assert.Equal(Segment(@"..\..\evil"), Segment(@"..\..\evil"));
}
