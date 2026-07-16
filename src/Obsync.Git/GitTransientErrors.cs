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
    private static readonly string[] PermanentMarkers =
    [
        "non-fast-forward",
        "failed to push some refs",
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
