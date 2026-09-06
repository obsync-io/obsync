using System.Net;
using System.Net.Http;
using Obsync.App.Services;

namespace Obsync.App.Tests;

/// <summary>
/// What the update notification tells the user, and how a rate-limited check is reported.
/// </summary>
/// <remarks>
/// The notification was a bare "a new version is available" and a browser link, while the product's
/// own installer source and INSTALL.md documented four preconditions that decide whether the
/// upgrade works. Pinned here because prose has no compiler.
/// </remarks>
public sealed class UpgradeGuidanceTests
{
    [Theory]
    // The upgrade replaces files this application has open.
    [InlineData("close this window")]
    // An open service list keeps the service delete pending -> ERROR_SERVICE_MARKED_FOR_DELETE,
    // which MSI misreports as a privileges problem.
    [InlineData("services.msc")]
    [InlineData("Server Manager")]
    // Windows will not return the service password, and an upgrade re-creates the service.
    [InlineData("password")]
    // Unsigned MSI: SmartScreen warns about an unrecognized publisher.
    [InlineData("code-signed")]
    public void TheAboutTabGuidance_CoversEveryPrecondition(string expected)
    {
        Assert.Contains(expected, UpgradeGuidance.BeforeYouInstall, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheToastNotice_NamesTheTwoThingsToClose()
    {
        // A toast has room for one sentence, so it carries the two causes of an outright upgrade
        // failure and leaves the rest to the About tab.
        Assert.Contains("Close Obsync", UpgradeGuidance.ShortNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("services", UpgradeGuidance.ShortNotice, StringComparison.OrdinalIgnoreCase);

        // Short enough to survive a 12-second toast without being truncated.
        Assert.True(
            UpgradeGuidance.ShortNotice.Length <= 90,
            $"The toast notice is {UpgradeGuidance.ShortNotice.Length} characters; keep it to one line.");
    }

    [Fact]
    public void AnExhaustedRateLimit_IsRecognized()
    {
        // 403 + x-ratelimit-remaining: 0 is how GitHub says "this whole network has used its hourly
        // allowance", which is not a problem with the machine, the proxy, or the credentials.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");

        Assert.True(UpdateChecker.IsRateLimited(response));
    }

    [Fact]
    public void A429_IsAlsoRecognized()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("x-ratelimit-remaining", "0");

        Assert.True(UpdateChecker.IsRateLimited(response));
    }

    [Fact]
    public void AnOrdinaryForbidden_IsNotMistakenForARateLimit()
    {
        // A 403 with budget left is a real authorization failure and needs a different answer.
        using var withBudget = new HttpResponseMessage(HttpStatusCode.Forbidden);
        withBudget.Headers.Add("x-ratelimit-remaining", "57");
        Assert.False(UpdateChecker.IsRateLimited(withBudget));

        using var withoutHeader = new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.False(UpdateChecker.IsRateLimited(withoutHeader));
    }

    [Fact]
    public void SuccessAndOtherFailures_AreNotRateLimits()
    {
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.False(UpdateChecker.IsRateLimited(ok));

        using var notFound = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.False(UpdateChecker.IsRateLimited(notFound));

        using var serverError = new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.False(UpdateChecker.IsRateLimited(serverError));
    }
}
