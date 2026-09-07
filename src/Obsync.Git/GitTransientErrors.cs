namespace Obsync.Git;

/// <summary>
/// Classifies transient git network failures (DNS blips, dropped connections, server 5xx/timeouts)
/// that are worth retrying. Permanent failures — rejected pushes (non-fast-forward), authentication
/// errors, missing repositories — are deliberately NOT treated as transient, so we never loop on them.
/// </summary>
public static class GitTransientErrors
{
    // Substrings (lowercased) seen in git's stderr for retryable network conditions.
    private static readonly string[] TransientMarkers =
    [
        "could not resolve host",
        "couldn't resolve host",
        "failed to connect",
        "connection timed out",
        "connection reset",
        "connection was reset",
        "operation timed out",
        "timed out",
        "rpc failed",
        "early eof",
        "the remote end hung up unexpectedly",
        "unable to access",        // transient TLS/proxy/HTTP transport errors
        "ssl_read",
        "gnutls_handshake",
        "http 500",
        "http 502",
        "http 503",
        "http 504",
        "error 503",
        "error 429",
        "remote error: internal server error",
        "temporary failure",
    ];

    // Substrings that mark a PERMANENT failure even if a transient marker also appears.
    //
    // "failed to push some refs" used to be here and must not be: it is git's generic TRAILER, not a
    // cause. It is printed for a policy rejection, for a non-fast-forward, AND for a dropped
    // connection — and because permanent markers are tested first and short-circuit, its presence
    // made every push failure permanent. The retry count offered in the job wizard therefore did
    // nothing for the operation users most expect it to cover. The real causes below are what should
    // decide, and each rejection GitHub can give now has its own entry.
    private static readonly string[] PermanentMarkers =
    [
        "non-fast-forward",
        "fetch first",
        // GitHub's push-policy rejections. Retrying any of these cannot help: the server answered,
        // and the answer will be the same next time.
        "gh001",                        // file over the size limit
        "gh006",                        // classic branch protection
        "gh013",                        // repository ruleset
        "repository rule violations",
        "protected branch",
        "push declined",
        "authentication failed",
        "permission denied",
        "repository not found",
        "could not read username",
        "terminal prompts disabled",
        // HTTP auth/permission/not-found from the smart-HTTP transport. These arrive inside a
        // "fatal: unable to access …" line, which is a transient marker — without these entries a
        // revoked token or a deleted repository would be retried with backoff instead of surfacing.
        "returned error: 401",
        "returned error: 403",
        "returned error: 404",
        // TLS policy. Every one of these is a deterministic decision made locally before a byte of
        // payload moves — a blocked CRL/OCSP responder, a corporate root the bundled ca-bundle.crt
        // does not carry, an expired or mismatched certificate. They all arrive inside a
        // "fatal: unable to access …" line, so without these entries a condition that cannot
        // possibly change within seconds was retried with backoff, and the user was shown a
        // "retrying" narrative for something retrying cannot fix.
        "schannel:",
        "ssl certificate problem",
        "certificate verify failed",
        "crypt_e_revocation_offline",
        "crypt_e_no_revocation_check",
        "sec_e_untrusted_root",
        "sec_e_cert_expired",
        "unable to get local issuer certificate",
        "self signed certificate",
        "ssl_error",
        // Proxy authentication. 401/403/404 were already permanent; 407 is the same class of
        // answer — the proxy rejected the credentials — and is what a service account gets when the
        // manual proxy password lives in a vault it cannot read.
        "returned error: 407",
        "received http code 407",
        "proxy authentication required",
    ];

    /// <summary>True when <paramref name="stderr"/> indicates a retryable network condition.</summary>
    public static bool IsTransient(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        var text = stderr.ToLowerInvariant();
        if (Array.Exists(PermanentMarkers, text.Contains))
        {
            return false;
        }

        return Array.Exists(TransientMarkers, text.Contains);
    }
}
