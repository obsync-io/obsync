using System.Net.Http;
using System.Security.Authentication;
using Obsync.GitHub;

namespace Obsync.Engine.Tests;

/// <summary>
/// What a failed GitHub REST call tells the user.
/// </summary>
/// <remarks>
/// Written against the exception chains .NET actually produces. The case that prompted it: a run in
/// pull-request mode cloned, committed and pushed over git without trouble, then failed to open the
/// PR with the complete and useless sentence "Could not reach GitHub: The SSL connection could not
/// be established, see inner exception." — a message whose own last three words name the thing the
/// code was throwing away.
/// </remarks>
public sealed class GitHubApiDiagnosisTests
{
    /// <summary>The chain .NET raises when the TLS handshake fails on certificate trust.</summary>
    private static HttpRequestException UntrustedCertificate() =>
        new("The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid according to the validation procedure.",
                new Exception("The certificate chain was issued by an authority that is not trusted")));

    [Fact]
    public void TheInnerException_IsNoLongerDiscarded()
    {
        var detail = GitHubApiDiagnosis.Describe(UntrustedCertificate());

        // The outer says which stage failed, the inner says why. Either alone is half a diagnosis.
        Assert.Contains("SSL connection could not be established", detail);
        Assert.Contains("not trusted", detail);
    }

    [Fact]
    public void DotNetsPointerToTheInnerException_IsNotRepeatedBackAtTheUser()
    {
        // "see inner exception" is an instruction, not information — and once followed it is noise.
        Assert.DoesNotContain("see inner exception", GitHubApiDiagnosis.Describe(UntrustedCertificate()));
    }

    [Fact]
    public void ACertificateFailure_SaysWhereTheTrustStoreIs_AndThatGitTlsDoesNotApply()
    {
        var reason = GitHubApiDiagnosis.Explain(UntrustedCertificate());

        Assert.Contains("Windows certificate store", reason);
        // The whole point of a separate explainer: git's advice ("change the TLS backend in
        // Settings → Network") cannot fix the REST client, which always uses Windows.
        Assert.Contains("do not apply", reason);
    }

    [Fact]
    public void AHandshakeFailure_PointsAtTheApiHostSpecifically()
    {
        // git talks to github.com and the REST client to api.github.com. A firewall allow-list that
        // permits one need not permit the other, and that is the likeliest cause when git works and
        // the API does not.
        var reason = GitHubApiDiagnosis.Explain(
            new HttpRequestException("The SSL connection could not be established, see inner exception."));

        Assert.Contains("api.github.com", reason);
        Assert.Contains("different", reason);
    }

    [Fact]
    public void ABlockedRevocationCheck_KeepsItsOwnRemedy()
    {
        // A certificate failure too, but with a different fix — so the broader certificate arm must
        // not swallow it.
        var reason = GitHubApiDiagnosis.Explain(
            new HttpRequestException("The SSL connection could not be established, see inner exception.",
                new Exception("The revocation function was unable to check revocation for the certificate.")));

        Assert.Contains("revocation", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("port 80", reason);
    }

    [Theory]
    [InlineData("No such host is known.", "DNS")]
    [InlineData("A connection attempt failed because the connected party did not properly respond, timed out", "firewall")]
    [InlineData("Received an HTTP 407 from the proxy after a CONNECT request", "proxy")]
    public void TheOtherTransportCauses_AreNamed(string message, string expected)
    {
        Assert.Contains(expected, GitHubApiDiagnosis.Explain(new HttpRequestException(message)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRawChain_IsAlwaysAvailableBehindTheExplanation()
    {
        // Support needs the underlying text even when the explanation is right, because the
        // explanation is a guess about cause and the chain is evidence.
        var reason = GitHubApiDiagnosis.Explain(UntrustedCertificate());

        Assert.Contains("Reported by Windows as:", reason);
        Assert.Contains("The remote certificate is invalid", reason);
    }

    [Fact]
    public void AnUnrecognisedFailure_StillReportsTheWholeChain()
    {
        var reason = GitHubApiDiagnosis.Explain(
            new HttpRequestException("Something new", new Exception("and its cause")));

        Assert.Contains("Something new", reason);
        Assert.Contains("and its cause", reason);
    }

    [Fact]
    public void ARepeatedMessage_IsNotPrintedTwice()
    {
        // .NET frequently wraps an exception in another carrying the identical message.
        var detail = GitHubApiDiagnosis.Describe(
            new HttpRequestException("Connection refused", new Exception("Connection refused")));

        Assert.Equal("Connection refused", detail);
    }

    [Fact]
    public void AnExceptionWithNoMessage_DoesNotProduceAnEmptyReason()
    {
        Assert.False(string.IsNullOrWhiteSpace(GitHubApiDiagnosis.Describe(new HttpRequestException(""))));
    }

    [Fact]
    public void APathologicalChain_IsBounded()
    {
        // Depth and length are both capped: this text is persisted into run rows, reports and
        // support bundles.
        Exception deep = new("level-0");
        for (var i = 1; i < 40; i++)
        {
            deep = new Exception(new string((char)('a' + i % 26), 80), deep);
        }

        var detail = GitHubApiDiagnosis.Describe(deep);

        Assert.True(detail.Length <= 501, $"Unbounded detail: {detail.Length} characters.");
    }
}
