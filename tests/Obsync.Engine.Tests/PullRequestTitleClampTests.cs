using Obsync.GitHub;

namespace Obsync.Engine.Tests;

/// <summary>
/// The API-boundary backstop on the pull request title. <see cref="CommitMessageBuilder"/> is
/// responsible for composing a title that already fits; this guarantees the invariant for every
/// caller of <see cref="IGitHubService"/> even if one of them composes its own title.
/// </summary>
public sealed class PullRequestTitleClampTests
{
    [Fact]
    public void ClampTitle_LeavesATitleThatFitsUntouched()
    {
        const string title = "[SalesDB] SQL object changes from PROD-SQL01 - 2026-06-28 23:00";

        Assert.Same(title, GitHubService.ClampTitle(title));
    }

    [Fact]
    public void ClampTitle_LeavesATitleAtExactlyTheLimitUntouched()
    {
        var title = new string('x', GitHubService.MaxPullRequestTitleLength);

        Assert.Same(title, GitHubService.ClampTitle(title));
    }

    [Fact]
    public void ClampTitle_BoundsAnOverlongTitle()
    {
        var clamped = GitHubService.ClampTitle(new string('x', 500));

        Assert.Equal(GitHubService.MaxPullRequestTitleLength, clamped.Length);
        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
    }

    [Fact]
    public void ClampTitle_NeverSplitsASurrogatePair()
    {
        // Cutting at the limit would land between the halves of a pair for an odd-length prefix,
        // so the clamp must step back a char rather than emit an unpaired surrogate.
        var clamped = GitHubService.ClampTitle(string.Concat(Enumerable.Repeat("\U0001D504", 400)));

        Assert.True(clamped.Length <= GitHubService.MaxPullRequestTitleLength);
        for (var i = 0; i < clamped.Length; i++)
        {
            if (char.IsHighSurrogate(clamped[i]))
            {
                Assert.True(i + 1 < clamped.Length && char.IsLowSurrogate(clamped[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(clamped[i]));
            }
        }
    }
}
