using Obsync.Git;

namespace Obsync.Git.Tests;

/// <summary>
/// Which push failures are worth retrying, and which are the server's final answer.
/// </summary>
/// <remarks>
/// <c>"failed to push some refs"</c> was in the permanent list. It is git's generic TRAILER, printed
/// for a policy rejection, a non-fast-forward AND a dropped connection alike — and because permanent
/// markers are tested first and short-circuit, its presence made every push failure permanent. The
/// retry count offered in the job wizard did nothing for the operation users most expect it to cover.
/// </remarks>
public sealed class PushReconciliationTests
{
    /// <summary>A connection dropped mid-push. git prints the trailer under the real cause.</summary>
    private const string DroppedConnection = """
        error: RPC failed; curl 56 Recv failure: Connection was reset
        send-pack: unexpected disconnect while reading sideband packet
        fatal: the remote end hung up unexpectedly
        error: failed to push some refs to 'https://github.com/acme/schema.git'
        """;

    [Fact]
    public void ADroppedConnectionDuringAPush_IsRetryable()
    {
        // The regression. Every one of these lines is present in a real transport failure, and the
        // trailer used to veto all of them.
        Assert.True(GitTransientErrors.IsTransient(DroppedConnection));
    }

    [Theory]
    // The server answered. Retrying cannot change the answer.
    [InlineData("remote: error: GH013: Repository rule violations found for refs/heads/main.\nerror: failed to push some refs")]
    [InlineData("remote: error: GH006: Protected branch update failed for refs/heads/main.\nerror: failed to push some refs")]
    [InlineData("remote: error: GH001: Large files detected.\nerror: failed to push some refs")]
    [InlineData(" ! [rejected] main -> main (non-fast-forward)\nerror: failed to push some refs")]
    [InlineData(" ! [remote rejected] main -> main (push declined due to repository rule violations)")]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/schema.git/'")]
    [InlineData("remote: Permission to acme/schema.git denied to bot.")]
    public void AServerRejection_IsNotRetryable(string stderr)
    {
        Assert.False(GitTransientErrors.IsTransient(stderr));
    }

    [Fact]
    public void APolicyRejectionThatAlsoLooksLikeATransportBlip_StaysPermanent()
    {
        // Permanent markers must keep beating transient ones when both appear — that ordering is why
        // the trailer was so damaging, and removing it must not weaken the rule itself.
        const string both = """
            remote: error: GH013: Repository rule violations found for refs/heads/main.
            fatal: the remote end hung up unexpectedly
            error: failed to push some refs to 'https://github.com/acme/schema.git'
            """;

        Assert.False(GitTransientErrors.IsTransient(both));
    }

    [Fact]
    public void TheGenericTrailer_AloneDecidesNothing()
    {
        // On its own it is not evidence of anything, in either direction.
        Assert.False(GitTransientErrors.IsTransient("error: failed to push some refs to 'https://x/y.git'"));
    }
}
