namespace Obsync.Shared.Models;

/// <summary>
/// How the bundled git validates TLS certificates. Machine-wide, alongside the proxy settings,
/// because it describes the network this installation sits on rather than anything per-job.
/// </summary>
/// <remarks>
/// Obsync ships MinGit precisely so nobody has to install git — and then, before this setting
/// existed, the only remedy for a git TLS problem was to locate that hidden binary and run
/// <c>git config</c> against it by hand. Two corporate configurations reach that dead end routinely:
/// a firewall blocking outbound port 80 to the CA's OCSP/CRL responder (schannel fails closed with
/// <c>CRYPT_E_REVOCATION_OFFLINE</c>), and TLS inspection with a private root that lives in the
/// Windows store but not in git's bundled CA list.
/// <para>
/// Neither is detectable from the REST API, which is why the Add Repository dialog could show five
/// green ticks and the first run still die on <c>git clone</c>: .NET's <c>HttpClient</c> does not
/// check revocation by default and reads the Windows store, so it succeeds where git fails.
/// </para>
/// </remarks>
public sealed class GitTlsSettings
{
    public GitTlsBackend Backend { get; set; } = GitTlsBackend.Default;

    /// <summary>
    /// Absolute path to a PEM CA bundle for <see cref="GitTlsBackend.OpenSsl"/>, sent as
    /// <c>http.sslCAInfo</c>. Use it when TLS inspection means git must trust a private root: export
    /// the corporate CA to a .crt/.pem file and point at it. Ignored by the schannel backends, which
    /// read the Windows store instead.
    /// </summary>
    public string? CaBundlePath { get; set; }
}
