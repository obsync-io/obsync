namespace Obsync.Shared.Models;

/// <summary>
/// Global HTTP/HTTPS proxy configuration for Obsync's outbound GitHub traffic (API + git). Shared by
/// the app and the Windows service. The proxy password is never stored here — it lives in Windows
/// Credential Manager, keyed by <see cref="Obsync.Shared.Abstractions.CredentialKeys.Proxy"/>.
/// </summary>
public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.None;

    /// <summary>The proxy URL for <see cref="ProxyMode.Manual"/>, e.g. <c>http://proxy.corp:8080</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Optional proxy username for an authenticated manual proxy.</summary>
    public string? Username { get; set; }

    /// <summary>Hosts that bypass the proxy (connect directly), e.g. <c>github.internal</c>.</summary>
    public List<string> BypassHosts { get; set; } = [];

    /// <summary>True when a proxy password should be present (authenticated manual proxy).</summary>
    public bool RequiresPassword => Mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(Username);
}
