using Obsync.Git;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// Which timeout a git command gets. The selector used to be "did the caller pass an environment
/// block" — that is, "does this command carry secrets", which has nothing to do with how long it
/// runs. So <c>add -A</c> over a first sync of an estate this codebase explicitly designs for
/// ("a VLDB run writes 100k+ script files", "a million-file first run") was given the two-minute
/// budget meant for index and ref operations.
///
/// The consequence was not a slow run but a permanently stuck job: the command was killed, the run
/// failed, state was correctly NOT advanced, so the next run repeated the identical work and failed
/// identically, forever.
/// </summary>
public sealed class GitCommandTimeoutTests
{
    private static readonly TimeSpan Cheap = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Network = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, string> NetworkEnvironment =
        new() { ["GIT_CONFIG_COUNT"] = "0" };

    [Theory]
    [InlineData("add")]
    [InlineData("commit")]
    [InlineData("checkout")]
    [InlineData("clean")]
    public void CommandsThatScaleWithTheWorkingTree_GetMoreThanTheCheapBudget(string verb)
    {
        var timeout = GitCommandRunner.SelectTimeout([verb, "-A", "--", "."], environment: null);

        Assert.True(timeout > Cheap, $"'{verb}' must not run on the cheap budget; got {timeout}.");
    }

    [Theory]
    [InlineData("config")]
    [InlineData("rev-parse")]
    [InlineData("remote")]
    [InlineData("check-ignore")]
    public void CheapCommands_KeepTheCheapBudget(string verb)
    {
        Assert.Equal(Cheap, GitCommandRunner.SelectTimeout([verb, "--get", "core.longpaths"], environment: null));
    }

    [Fact]
    public void NetworkCommands_AreRecognisedByTheirEnvironmentBlock()
    {
        // Only RunNetworkAsync supplies one, and it always does — the auth header and proxy live there.
        Assert.Equal(Network, GitCommandRunner.SelectTimeout(["fetch", "origin"], NetworkEnvironment));
        Assert.Equal(Network, GitCommandRunner.SelectTimeout(["ls-remote", "--heads"], NetworkEnvironment));
    }

    [Fact]
    public void LeadingDashCArgumentsAreSkippedWhenFindingTheVerb()
    {
        // `commit` is issued as: -c user.name=… -c user.email=… commit --no-gpg-sign …
        // Reading the first argument as the verb would classify every commit as cheap.
        var timeout = GitCommandRunner.SelectTimeout(
            ["-c", "user.name=Obsync", "-c", "user.email=obsync@localhost", "commit", "--no-gpg-sign", "-m", "x"],
            environment: null);

        Assert.True(timeout > Cheap);
    }

    [Fact]
    public void AnEmptyArgumentList_FallsBackToTheCheapBudget()
    {
        Assert.Equal(Cheap, GitCommandRunner.SelectTimeout([], environment: null));
        Assert.Equal(Cheap, GitCommandRunner.SelectTimeout(["--version"], environment: null));
    }
}
