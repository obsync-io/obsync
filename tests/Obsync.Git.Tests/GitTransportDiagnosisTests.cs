using Obsync.Git;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// git reports a whole family of unrelated problems behind one line —
/// <c>fatal: unable to access '…': &lt;detail&gt;</c> — so matching that line first produced
/// "check network connectivity" for certificate-trust failures, blocked revocation responders and
/// rejected proxy credentials alike. Each of those sends the reader somewhere useless. These pin
/// the ordering, using stderr captured from the real failures.
/// </summary>
public sealed class GitTransportDiagnosisTests
{
    private const string RevocationOffline =
        "fatal: unable to access 'https://github.com/o/r.git/': schannel: next InitializeSecurityContext failed: "
        + "CRYPT_E_REVOCATION_OFFLINE (0x80092013) - The revocation function was unable to check revocation because "
        + "the revocation server was offline.";

    [Fact]
    public void ABlockedRevocationResponder_IsNotReportedAsAConnectivityProblem()
    {
        var diagnosis = GitTransportDiagnosis.Explain(RevocationOffline);

        Assert.Contains("revocation", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("port 80", diagnosis);
        Assert.DoesNotContain("check network connectivity", diagnosis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUntrustedRoot_PointsAtTheTlsBackendRatherThanTheNetwork()
    {
        var diagnosis = GitTransportDiagnosis.Explain(
            "fatal: unable to access 'https://github.com/o/r.git/': SSL certificate problem: unable to get local "
            + "issuer certificate");

        Assert.Contains("did not trust", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", diagnosis);
    }

    [Fact]
    public void AProxy407_NamesThePerAccountCredentialVault()
    {
        // The signature of a service account that cannot read the proxy password the app saved:
        // manual runs work and every scheduled run fails.
        var diagnosis = GitTransportDiagnosis.Explain(
            "fatal: unable to access 'https://github.com/o/r.git/': Received HTTP code 407 from proxy after CONNECT");

        Assert.Contains("proxy", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per Windows account", diagnosis);
    }

    [Fact]
    public void A404_ExplainsWhyAValidTokenCannotSeeTheRepository()
    {
        var diagnosis = GitTransportDiagnosis.Explain(
            "fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 404");

        Assert.Contains("SSO", diagnosis);
    }

    [Fact]
    public void APlainConnectivityFailure_StillGetsTheConnectivityAnswer()
    {
        var diagnosis = GitTransportDiagnosis.Explain(
            "fatal: unable to access 'https://github.com/o/r.git/': Failed to connect to github.com port 443: Timed out");

        Assert.Contains("Could not reach", diagnosis);
    }

    [Fact]
    public void SomethingUnrecognised_ReturnsNullFromTryExplain()
    {
        // So a caller with more specific knowledge (the engine's push explanation) can try its own
        // arms first rather than being pre-empted by a generic answer.
        Assert.Null(GitTransportDiagnosis.TryExplain("fatal: the remote end hung up unexpectedly"));
    }

    [Fact]
    public void TheseFailuresAreNotRetryable()
    {
        // Every one is a deterministic local decision made before any payload moves, so retrying
        // cannot change the answer — it only delays the error and shows the user a misleading
        // "retrying" narrative. They arrive inside a transient-marked "unable to access" line, so
        // this is the classification that has to override it.
        Assert.False(GitTransientErrors.IsTransient(RevocationOffline));
        Assert.False(GitTransientErrors.IsTransient(
            "fatal: unable to access 'https://x/': SSL certificate problem: unable to get local issuer certificate"));
        Assert.False(GitTransientErrors.IsTransient(
            "fatal: unable to access 'https://x/': Received HTTP code 407 from proxy after CONNECT"));

        // ...while a genuine blip stays retryable.
        Assert.True(GitTransientErrors.IsTransient("error: RPC failed; HTTP 503 curl 22"));
        Assert.True(GitTransientErrors.IsTransient("fatal: unable to access 'https://x/': Connection timed out"));
    }
}
