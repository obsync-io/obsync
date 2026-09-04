using Obsync.Shared;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// The redaction rule that guards text outliving the process — persisted run errors, logged command
/// lines, and the support bundle. The rule it replaces required a <c>user:password</c> pair, so it
/// let through the two userinfo shapes that omit one: <c>https://&lt;token&gt;@host</c>, which is the
/// form GitHub's documentation produces, and <c>http://user:@host</c>, which this product builds
/// itself when a proxy has a username but no stored password.
///
/// The token strings here are deliberately not real credentials — they are the right shape and
/// nothing more.
/// </summary>
public sealed class SecretRedactorTests
{
    private const string FakeClassicToken = "ghp_000000000000000000000000000000000000";
    private const string FakeFineGrained = "github_pat_00000000000000000000_0000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Scrub_TokenOnlyUserInfo_IsRedacted()
    {
        // The gap this rule was widened for.
        var scrubbed = SecretRedactor.Scrub($"fatal: unable to access 'https://{FakeClassicToken}@github.com/o/r.git/'");

        Assert.DoesNotContain(FakeClassicToken, scrubbed);
        Assert.Contains("https://***@github.com/o/r.git", scrubbed);
    }

    [Fact]
    public void Scrub_UserAndPasswordUserInfo_IsStillRedacted()
    {
        // The shape the previous rule handled; it must keep working.
        var scrubbed = SecretRedactor.Scrub("Failed to connect to http://alice:s3cret@proxy:8080/");

        Assert.DoesNotContain("s3cret", scrubbed);
        Assert.DoesNotContain("alice", scrubbed);
        Assert.Contains("http://***@proxy:8080/", scrubbed);
    }

    [Fact]
    public void Scrub_EmptyPasswordUserInfo_IsRedacted()
    {
        // ProxyProvider composes "{scheme}://{user}:{password}@{host}" with the password defaulting
        // to empty, so a proxy configured with a username and no stored password produces exactly
        // this — and the previous rule, which required a non-empty password, missed it.
        var scrubbed = SecretRedactor.Scrub("Failed to connect to http://alice:@proxy:8080/");

        Assert.DoesNotContain("alice", scrubbed);
        Assert.Contains("http://***@proxy:8080/", scrubbed);
    }

    [Theory]
    [InlineData("gho_000000000000000000000000000000000000")]
    [InlineData("ghu_000000000000000000000000000000000000")]
    [InlineData("ghs_000000000000000000000000000000000000")]
    [InlineData("ghr_000000000000000000000000000000000000")]
    public void Scrub_EveryGitHubTokenPrefix_IsRedactedAnywhereInTheText(string token)
    {
        // Not only inside a URL: a token echoed by a tool or quoted back in an API error is the
        // same secret.
        var scrubbed = SecretRedactor.Scrub($"remote: Invalid credentials for {token} — try again");

        Assert.DoesNotContain(token, scrubbed);
        Assert.Contains("***", scrubbed);
    }

    [Fact]
    public void Scrub_FineGrainedToken_IsRedacted()
    {
        var scrubbed = SecretRedactor.Scrub($"Authorization failed using {FakeFineGrained}.");

        Assert.DoesNotContain(FakeFineGrained, scrubbed);
    }

    [Theory]
    [InlineData("fatal: repository 'https://github.com/torvalds/linux.git/' not found")]
    [InlineData("Author: someone@example.com committed the change")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("error: pathspec 'schemas/dbo@2.sql' did not match any file")]
    public void Scrub_TextWithNoCredentials_IsUnchanged(string text)
    {
        // Over-redaction costs diagnosability, which is the whole reason these strings are kept.
        // A clean URL, a bare email address and an '@' in a path must all survive.
        Assert.Equal(text, SecretRedactor.Scrub(text));
    }

    [Fact]
    public void Scrub_KeepsTheHostAndPath_SoTheMessageStaysDiagnostic()
    {
        var scrubbed = SecretRedactor.Scrub($"fatal: unable to access 'https://{FakeClassicToken}@ghe.corp.local/team/db.git/': 403");

        Assert.Contains("ghe.corp.local/team/db.git", scrubbed);
        Assert.Contains("403", scrubbed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Scrub_NullOrEmpty_PassesThrough(string? text)
    {
        Assert.Equal(text, SecretRedactor.Scrub(text));
    }

    [Theory]
    [InlineData("https://ghp_000000000000000000000000000000000000@github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData("https://user:pass@github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData("https://github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData(@"C:\workspaces\origin.git", @"C:\workspaces\origin.git")]
    public void StripUrlCredentials_RemovesThemEntirelyRatherThanMasking(string url, string expected)
    {
        // Masking is not enough for a URL about to become a git command-line argument: Windows
        // process-creation auditing records argv verbatim and git writes the remote into
        // .git/config, and neither copy comes back through Obsync to be scrubbed later.
        Assert.Equal(expected, SecretRedactor.StripUrlCredentials(url));
    }

    [Fact]
    public void StripUrlCredentials_LeavesAUsableRemote()
    {
        // The stripped URL must still be the same remote — authentication comes from the injected
        // header, so removing the userinfo costs nothing.
        var stripped = SecretRedactor.StripUrlCredentials(
            "https://ghp_000000000000000000000000000000000000@ghe.corp.local:8443/team/db.git");

        Assert.Equal("https://ghe.corp.local:8443/team/db.git", stripped);
    }

    [Fact]
    public void Scrub_ShortGhLookingWord_IsNotRedacted()
    {
        // The prefixes need 36+ trailing characters to count, so ordinary prose is safe.
        Assert.Equal("the ghp_short marker", SecretRedactor.Scrub("the ghp_short marker"));
    }
}
