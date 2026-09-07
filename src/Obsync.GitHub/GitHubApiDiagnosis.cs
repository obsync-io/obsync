using System.Text;

namespace Obsync.GitHub;

/// <summary>
/// Turns an exception from a GitHub REST call into something a user can act on.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>GitTransportDiagnosis</c>, which does the same job for git. They
/// look like duplicates and are not: the two halves of this product reach GitHub over different
/// stacks, to different hostnames, with different trust stores and different settings.
///
/// <list type="table">
/// <item><term>git</term><description><c>github.com</c>, bundled MinGit, schannel or OpenSSL as chosen in Settings → Network, OpenSSL using MinGit's own CA list.</description></item>
/// <item><term>REST</term><description><c>api.github.com</c>, .NET <c>HttpClient</c>, always schannel, always the Windows certificate store.</description></item>
/// </list>
///
/// <para>
/// So the remedies differ, and reusing git's explainer here would hand out advice that cannot work:
/// its certificate arm says "set the TLS backend, or supply your CA bundle, in Settings → Network",
/// and that setting has no effect whatsoever on the REST client. Sending someone to change it while
/// the API is the failing half is the same class of mistake as diagnosing a repository policy as a
/// stale branch.
/// </para>
///
/// <para>
/// It also exists because .NET hides the answer one level down. <see cref="HttpRequestException"/>
/// for a TLS failure reads, in full, "The SSL connection could not be established, see inner
/// exception." — and every catch in <c>GitHubService</c> reported that and nothing else. The
/// sentence is an instruction to look further, and the code did not follow it.
/// </para>
/// </remarks>
public static class GitHubApiDiagnosis
{
    /// <summary>Guard against a pathological chain; five is far more than any real one.</summary>
    private const int MaxDepth = 5;

    /// <summary>Keeps the persisted message bounded — it is copied into run rows and reports.</summary>
    private const int MaxLength = 500;

    /// <summary>
    /// The full exception chain as one line, innermost detail included.
    /// </summary>
    /// <remarks>
    /// The outer message is kept as well as the inner: the outer says which stage failed ("The SSL
    /// connection could not be established") and the inner says why ("The certificate chain was
    /// issued by an authority that is not trusted"). Either alone is half a diagnosis.
    /// </remarks>
    public static string Describe(Exception exception)
    {
        var parts = new List<string>();
        var current = (Exception?)exception;

        for (var depth = 0; current is not null && depth < MaxDepth; depth++, current = current.InnerException)
        {
            var message = Trim(current.Message);
            if (message.Length == 0
                || parts.Any(p => p.Equals(message, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            parts.Add(message);
        }

        if (parts.Count == 0)
        {
            return exception.GetType().Name;
        }

        var joined = string.Join(" — ", parts);
        return joined.Length <= MaxLength ? joined : joined[..MaxLength] + "…";
    }

    /// <summary>
    /// Strips .NET's pointer to the inner exception, which carries no information once it has been
    /// followed — and reads as a dead end when it has not.
    /// </summary>
    private static string Trim(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        const string pointer = "see inner exception.";

        if (text.EndsWith(pointer, StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^pointer.Length].TrimEnd(' ', ',', ';');
        }

        return text;
    }

    /// <summary>
    /// An actionable cause for a REST failure, or null when the chain does not describe one this
    /// build recognises — letting the caller fall back to <see cref="Describe"/>.
    /// </summary>
    public static string? TryExplain(Exception exception)
    {
        var text = Describe(exception).ToLowerInvariant();

        // Revocation first: it is a certificate failure too, but with its own remedy, so a broader
        // certificate arm above it would swallow the specific advice.
        if (text.Contains("revocation"))
        {
            return "Windows could not check the certificate's revocation status and refused the connection. "
                 + "This usually means outbound port 80 to the certificate authority's OCSP/CRL responder is "
                 + "blocked. Note this is the Windows TLS stack, which the Git TLS setting does not affect — "
                 + "the fix has to come from the network, not from Obsync.";
        }

        if (text.Contains("not trusted") || text.Contains("remote certificate is invalid")
            || text.Contains("untrusted") || text.Contains("partial chain")
            || text.Contains("certificate chain"))
        {
            return "Windows did not trust api.github.com's certificate. If this network inspects TLS, its private "
                 + "root has to be installed in the Windows certificate store (Local Machine → Trusted Root "
                 + "Certification Authorities) — the REST client always uses the Windows store, so the Git TLS "
                 + "setting and any CA bundle configured there do not apply to it.";
        }

        if (text.Contains("tls alert") || text.Contains("handshakefailure")
            || text.Contains("ssl connection could not be established"))
        {
            return "The TLS handshake with api.github.com failed before any data was exchanged. Git reaches "
                 + "github.com over its own TLS stack, so this can fail while git succeeds — they are different "
                 + "hosts and different stacks. Check whether api.github.com specifically is permitted by the "
                 + "firewall or proxy, and whether this machine restricts TLS protocols or ciphers.";
        }

        if (text.Contains("407") || text.Contains("proxy authentication"))
        {
            return "The HTTP proxy rejected Obsync's credentials. Proxy passwords are stored per Windows account, "
                 + "so a scheduled run fails here when the service's account cannot read the one the app saved — "
                 + "re-save it in Settings → Network while signed in as that account.";
        }

        if (text.Contains("no such host") || text.Contains("name or service not known")
            || text.Contains("could not resolve"))
        {
            return "api.github.com could not be resolved by DNS. Check this machine's DNS configuration and any "
                 + "split-horizon or filtering resolver on the network.";
        }

        if (text.Contains("timed out") || text.Contains("timeout")
            || text.Contains("actively refused") || text.Contains("unable to connect"))
        {
            return "Could not open a connection to api.github.com. Check the firewall and proxy allow outbound "
                 + "HTTPS to that host specifically — an allow-list that permits github.com does not necessarily "
                 + "permit api.github.com.";
        }

        return null;
    }

    /// <summary>
    /// The user-facing reason for a failed REST call: an explanation when one is recognised, with
    /// the raw chain appended so support always has the underlying text.
    /// </summary>
    public static string Explain(Exception exception)
    {
        var detail = Describe(exception);
        return TryExplain(exception) is { } cause
            ? $"{cause} (Reported by Windows as: {detail})"
            : detail;
    }
}
