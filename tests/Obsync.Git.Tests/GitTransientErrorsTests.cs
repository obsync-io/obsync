namespace Obsync.Git.Tests;

public sealed class GitTransientErrorsTests
{
    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com")]
    [InlineData("error: RPC failed; curl 56 Recv failure: Connection reset by peer")]
    [InlineData("fatal: unable to access '...': The requested URL returned error: 503")]
    [InlineData("ssh: connect to host github.com port 22: Connection timed out")]
    [InlineData("fatal: the remote end hung up unexpectedly")]
    public void IsTransient_NetworkConditions_ReturnsTrue(string stderr) =>
        Assert.True(GitTransientErrors.IsTransient(stderr));

    [Theory]
    [InlineData("! [rejected]        main -> main (non-fast-forward)")]
    [InlineData("fatal: Authentication failed for 'https://github.com/x/y.git/'")]
    [InlineData("remote: Repository not found.")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    // HTTP auth/permission/not-found arrive inside "unable to access" (a transient marker); the
    // permanent status-code markers must win so a revoked token is not retried with backoff.
    [InlineData("fatal: unable to access 'https://github.com/x/y.git/': The requested URL returned error: 401")]
    [InlineData("fatal: unable to access 'https://github.com/x/y.git/': The requested URL returned error: 403")]
    [InlineData("fatal: unable to access 'https://github.com/x/y.git/': The requested URL returned error: 404")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTransient_PermanentOrEmpty_ReturnsFalse(string? stderr) =>
        Assert.False(GitTransientErrors.IsTransient(stderr));
}
