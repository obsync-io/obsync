using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

/// <summary>
/// Containment for every path the engine writes, copies or deletes. A database name is read from
/// <c>sys.databases</c> and a destination folder can arrive in an imported file, so composition
/// takes untrusted input; this is the guard that keeps the result inside the workspace.
/// </summary>
public sealed class WorkspaceContainmentTests
{
    private const string Root = @"C:\obsync\ws";

    [Theory]
    [InlineData("sql/db/procedures/dbo.x.sql")]
    [InlineData(@"sql\db\procedures\dbo.x.sql")]
    [InlineData("a.sql")]
    public void ResolvesPathsInsideTheRoot(string relative)
    {
        var resolved = RepositoryLayout.ResolveWithin(Root, relative);

        Assert.NotNull(resolved);
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeparatorStyleDoesNotMatter()
    {
        Assert.Equal(
            RepositoryLayout.ResolveWithin(Root, "sql/db/x.sql"),
            RepositoryLayout.ResolveWithin(Root, @"sql\db\x.sql"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil.sql")]
    [InlineData(@"..\evil.sql")]
    [InlineData("sql/../../evil.sql")]
    [InlineData(@"..\..\..\..\..\..\Users\Public\x.sql")]
    public void RejectsTraversal(string relative) =>
        Assert.Null(RepositoryLayout.ResolveWithin(Root, relative));

    [Theory]
    [InlineData(@"C:\Windows\evil.sql")]
    [InlineData(@"\Windows\evil.sql")]
    [InlineData(@"\\server\share\evil.sql")]
    public void RejectsRootedPaths(string relative) =>
        Assert.Null(RepositoryLayout.ResolveWithin(Root, relative));

    [Fact]
    public void RejectsADriveRelativePathOnAnotherVolume() =>
        // "C:evil.sql" is rooted but not fully qualified, so it escapes a D: root entirely.
        Assert.Null(RepositoryLayout.ResolveWithin(@"D:\root", "C:evil.sql"));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("sql/..")]
    public void RejectsAnythingResolvingToTheRootItself(string relative) =>
        // Every composed path here is a file or a subfolder; collapsing to the root means the
        // composition is broken, not that the write is legitimate.
        Assert.Null(RepositoryLayout.ResolveWithin(Root, relative));

    [Theory]
    [InlineData(@"C:\obsync\ws2\evil.sql")]
    [InlineData(@"C:\obsync\wsX\evil.sql")]
    public void RejectsASiblingDirectoryThatSharesThePrefix(string absolute) =>
        // The regression a naive StartsWith(root) would fail: "C:\obsync\ws2" starts with
        // "C:\obsync\ws" as a string but is a different directory.
        Assert.Null(RepositoryLayout.ResolveWithin(Root, absolute));

    [Fact]
    public void AcceptsAPathDifferingOnlyByCase() =>
        // NTFS is case-insensitive, so this really is the same location; an ordinal comparison
        // would reject a legitimate path.
        Assert.NotNull(RepositoryLayout.ResolveWithin(@"C:\OBSYNC\WS", "a.sql"));

    [Fact]
    public void ATrailingSeparatorOnTheRootDoesNotChangeTheResult() =>
        Assert.Equal(
            RepositoryLayout.ResolveWithin(Root, "a.sql"),
            RepositoryLayout.ResolveWithin(Root + @"\", "a.sql"));

    [Fact]
    public void RejectsAnEmbeddedNullWithoutThrowing() =>
        Assert.Null(RepositoryLayout.ResolveWithin(Root, "sql\0/x.sql"));
}
