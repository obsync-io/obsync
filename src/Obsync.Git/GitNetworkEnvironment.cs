using Obsync.Shared;

namespace Obsync.Git;

/// <summary>Everything that shapes one authenticated git network call, independent of a workspace.</summary>
/// <param name="RemoteUrl">The remote being contacted; scopes the auth header.</param>
/// <param name="AuthorizationHeader">Full HTTP header value, e.g. "AUTHORIZATION: basic &lt;base64&gt;". Never logged.</param>
/// <param name="ProxyUrl">HTTP proxy URL, possibly with credentials; null for a direct connection.</param>
/// <param name="TlsBackend">Which TLS stack git validates with.</param>
/// <param name="CaBundlePath">PEM CA bundle for the OpenSSL backend; ignored otherwise.</param>
public sealed record GitNetworkOptions(
    string? RemoteUrl,
    string? AuthorizationHeader,
    string? ProxyUrl,
    GitTlsBackend TlsBackend = GitTlsBackend.Default,
    string? CaBundlePath = null);

/// <summary>
/// Builds the <c>GIT_CONFIG_*</c> environment block for a git command that talks to a remote.
/// </summary>
/// <remarks>
/// Extracted so the preflight/diagnostics reachability probe and the real clone/fetch/push are
/// configured by the SAME code. That is the entire point of the probe: a check that authenticates
/// differently, or proxies differently, or trusts differently from the run it is vouching for
/// cannot vouch for it — which is how the Add Repository dialog came to show five green ticks
/// immediately before <c>git clone</c> failed on TLS.
/// <para>
/// Secrets travel as ENVIRONMENT variables, never as <c>-c</c> arguments: Windows process-creation
/// auditing (Event 4688, Sysmon, EDR) records child command lines verbatim into machine-wide
/// security logs, and would capture the token and any proxy credentials. Environment blocks are not
/// captured. Nothing here is written to <c>.git/config</c>.
/// </para>
/// </remarks>
public static class GitNetworkEnvironment
{
    public static Dictionary<string, string> Build(GitNetworkOptions options)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddConfig(string key, string value)
        {
            var index = environment.Count / 2;
            environment[$"GIT_CONFIG_KEY_{index}"] = key;
            environment[$"GIT_CONFIG_VALUE_{index}"] = value;
        }

        // Scope the header to the remote we mean to authenticate to. Unscoped, `http.extraheader`
        // applies to whatever host the command ends up contacting — and `url.<other>.insteadOf` in
        // the machine's config silently rewrites the remote before the request is made, so the
        // token was delivered to the rewritten host on the first request, with the user seeing only
        // "repository not found". An internal mirror or proxy pushed by ordinary config management
        // is enough; no attacker is required. Measured both ways: unscoped, the header arrives at
        // the rewritten host; scoped, it is withheld there and still sent to the real remote.
        if (!string.IsNullOrEmpty(options.AuthorizationHeader)
            && HttpScopePrefix(options.RemoteUrl) is { } scope)
        {
            AddConfig($"http.{scope}.extraheader", options.AuthorizationHeader);
        }

        // Route network operations through the configured proxy (may carry credentials).
        if (!string.IsNullOrEmpty(options.ProxyUrl))
        {
            AddConfig("http.proxy", options.ProxyUrl);
        }

        switch (options.TlsBackend)
        {
            case GitTlsBackend.Schannel:
                AddConfig("http.sslBackend", "schannel");
                break;

            case GitTlsBackend.SchannelNoRevocationCheck:
                // Revocation checking needs outbound port 80 to the CA's OCSP/CRL responder, which
                // corporate egress policies commonly block. schannel then fails CLOSED — correct,
                // but it stops every clone with CRYPT_E_REVOCATION_OFFLINE. This keeps the Windows
                // trust store (so a GPO-deployed corporate root still works) and downgrades only the
                // revocation step.
                AddConfig("http.sslBackend", "schannel");
                AddConfig("http.schannelCheckRevoke", "false");
                break;

            case GitTlsBackend.OpenSsl:
                AddConfig("http.sslBackend", "openssl");
                if (!string.IsNullOrWhiteSpace(options.CaBundlePath))
                {
                    // OpenSSL cannot see the Windows certificate store, so on a TLS-inspecting
                    // network the private root has to be supplied as a file or nothing validates.
                    AddConfig("http.sslCAInfo", options.CaBundlePath);
                }

                break;

            case GitTlsBackend.Default:
            default:
                break;
        }

        if (environment.Count > 0)
        {
            environment["GIT_CONFIG_COUNT"] = (environment.Count / 2).ToString();
        }

        return environment;
    }

    /// <summary>
    /// The <c>scheme://host[:port]/</c> prefix an <c>http.&lt;url&gt;.*</c> key must carry to apply to this
    /// remote, or null when the remote is not HTTP(S) — in which case an HTTP auth header is
    /// meaningless and is simply not sent. git matches these keys by longest URL prefix, so the
    /// origin prefix covers every path under it while excluding any other host.
    /// </summary>
    internal static string? HttpScopePrefix(string? remoteUrl)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        // Cut the ORIGINAL string rather than rebuilding from the parsed Uri: Uri normalizes an
        // explicitly written default port away, so "https://host:443/x" would yield "https://host/"
        // — and if git matches ports strictly the header would silently not be sent and the push
        // would fail to authenticate. Slicing guarantees the scope is a literal prefix of the URL
        // git is handed, whatever either side normalizes.
        var afterScheme = remoteUrl!.IndexOf("://", StringComparison.Ordinal) + 3;
        var slash = remoteUrl.IndexOf('/', afterScheme);
        return slash < 0 ? remoteUrl + "/" : remoteUrl[..(slash + 1)];
    }
}
