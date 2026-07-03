using System.Net;
using Obsync.Shared.Results;

namespace Obsync.Shared.Abstractions;

/// <summary>An effective proxy resolved for Obsync's outbound GitHub traffic.</summary>
/// <param name="WebProxy">For the GitHub API (Octokit's HttpClientHandler).</param>
/// <param name="GitProxyUrl">For the git CLI (<c>-c http.proxy=…</c>); null when git should go direct.</param>
public sealed record ProxyResolution(IWebProxy WebProxy, string? GitProxyUrl);

/// <summary>Resolves and tests the configured HTTP/HTTPS proxy for outbound GitHub calls.</summary>
public interface IProxyProvider
{
    /// <summary>
    /// The effective proxy for GitHub, or <c>null</c> when connecting directly (mode None, or System
    /// with no OS proxy for GitHub).
    /// </summary>
    Task<ProxyResolution?> ResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>Tests whether GitHub is reachable through the configured proxy.</summary>
    Task<Result> TestAsync(CancellationToken cancellationToken = default);
}
