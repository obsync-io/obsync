namespace Obsync.Git;

/// <summary>
/// Turns raw git stderr into a cause the user can act on.
/// </summary>
/// <remarks>
/// Shared by the reachability probe and the engine's push-failure explanation so both name the same
/// cause the same way. It exists because git reports a whole family of distinct, unrelated problems
/// behind one line — <c>fatal: unable to access '…': &lt;detail&gt;</c> — and matching that line
/// first produced "check network connectivity" for certificate-trust failures, proxy credential
/// rejections and blocked revocation responders alike. Every one of those sends the reader somewhere
/// useless. The specific arms therefore run BEFORE the connectivity catch-all, which is the ordering
/// rule this class exists to enforce in one place.
/// </remarks>
public static class GitTransportDiagnosis
{
    /// <summary>
    /// The transport-level cause, or <c>null</c> when <paramref name="stderr"/> is not one — letting
    /// a caller with more specific knowledge (a push, say) try its own arms first.
    /// </summary>
    public static string? TryExplain(string? stderr)
    {
        var text = (stderr ?? string.Empty).ToLowerInvariant();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("crypt_e_revocation_offline") || text.Contains("crypt_e_no_revocation_check")
            || text.Contains("revocation"))
        {
            return "The network blocked git's certificate revocation check — the certificate authority's OCSP/CRL "
                 + "responder was unreachable, and the Windows TLS stack refuses the connection rather than skipping "
                 + "the check. Allow outbound port 80 to the CA's endpoints, or change the TLS backend in "
                 + "Settings → Network.";
        }

        if (text.Contains("unable to get local issuer certificate") || text.Contains("sec_e_untrusted_root")
            || text.Contains("self signed certificate") || text.Contains("ssl certificate problem")
            || text.Contains("certificate verify failed"))
        {
            return "git did not trust the server's certificate. This usually means the network inspects TLS with a "
                 + "private root: the Windows TLS backend sees roots deployed by Group Policy, while git's bundled "
                 + "certificate list does not. Set the TLS backend to Windows (schannel), or supply your CA bundle, "
                 + "in Settings → Network.";
        }

        if (text.Contains("407") || text.Contains("proxy authentication required"))
        {
            return "The HTTP proxy rejected Obsync's credentials. Proxy passwords are stored per Windows account, so "
                 + "a scheduled run fails here when the service's account cannot read the one the app saved — "
                 + "re-save it in Settings → Network while signed in as that account.";
        }

        if (text.Contains("schannel:") || text.Contains("ssl_error") || text.Contains("gnutls_handshake"))
        {
            return "The TLS connection failed before any data was transferred. See technical details for the exact "
                 + "error, and check Settings → Network if this machine inspects or filters TLS.";
        }

        if (text.Contains("could not read username") || text.Contains("terminal prompts disabled")
            || text.Contains("authentication failed") || text.Contains("returned error: 401"))
        {
            return "GitHub rejected the credentials — check the repository's access token is valid and not expired.";
        }

        if (text.Contains("returned error: 403") || text.Contains("permission denied"))
        {
            return "GitHub denied access — the access token needs write (Contents) permission on this repository, "
                 + "and on an organisation with SSO it must also be authorised for that organisation.";
        }

        if (text.Contains("repository not found") || text.Contains("returned error: 404"))
        {
            return "GitHub reported the repository as not found. With a valid token this usually means the token "
                 + "cannot SEE it: a fine-grained token awaiting organisation approval, a resource owner set to a "
                 + "personal account, or a classic token not yet authorised for the organisation's SSO.";
        }

        if (text.Contains("could not resolve host"))
        {
            return "The repository host could not be resolved by DNS — check the repository URL and this machine's "
                 + "network configuration.";
        }

        if (text.Contains("failed to connect") || text.Contains("connection timed out") || text.Contains("timed out")
            || text.Contains("unable to access"))
        {
            return "Could not reach the repository host — check network connectivity, any proxy setting, and the "
                 + "repository URL.";
        }

        return null;
    }

    /// <summary>As <see cref="TryExplain"/>, falling back to git's own first line.</summary>
    public static string Explain(string? stderr)
    {
        if (TryExplain(stderr) is { } diagnosis)
        {
            return diagnosis;
        }

        var firstLine = (stderr ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "git reported no detail for the failure." : firstLine;
    }
}
