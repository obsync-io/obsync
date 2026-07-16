using Obsync.Shared;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// <see cref="GitRefName.IsValidBranchName"/> mirrors the <c>git check-ref-format</c> rules the
/// wizard's Destination step relies on to reject branches that would only fail inside the git CLI.
/// </summary>
public sealed class GitRefNameTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/x")]
    [InlineData("release-1.0")]
    [InlineData("a.b")]                    // a dot inside a component is fine
    [InlineData("feature/deep/nesting")]
    [InlineData("UPPER-and-lower_123")]
    [InlineData("v1.2.3")]
    [InlineData("a.locker")]               // ".lock" only forbidden as a component SUFFIX
    public void IsValidBranchName_AcceptsValidNames(string name)
    {
        Assert.True(GitRefName.IsValidBranchName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]      // no spaces
    [InlineData("a..b")]     // no double dots
    [InlineData("/a")]       // no leading slash
    [InlineData("a/")]       // no trailing slash
    [InlineData("a//b")]     // no empty component
    [InlineData("~x")]       // forbidden character
    [InlineData("a^b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData(@"a\b")]     // no backslash
    [InlineData("@")]        // the single '@' is reserved
    [InlineData("a@{b")]     // no '@{' sequence
    [InlineData(".a")]       // component must not start with '.'
    [InlineData("x/.hidden")]
    [InlineData("a.lock")]   // component must not end with '.lock'
    [InlineData("a.lock/b")]
    [InlineData("a.")]       // no trailing dot
    [InlineData("a\tb")]     // control character
    public void IsValidBranchName_RejectsInvalidNames(string? name)
    {
        Assert.False(GitRefName.IsValidBranchName(name));
    }
}
