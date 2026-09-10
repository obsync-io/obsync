using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;

namespace Obsync.Data;

/// <inheritdoc cref="IProxyProvider" />
public sealed class ProxyProvider : IProxyProvider
{
    private static readonly Uri GitHubApi = new("https://api.github.com");
    private static readonly Uri GitHubRemote = new("https://github.com");

    private readonly IAppSettingsRepository _settings;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<ProxyProvider> _logger;

    public ProxyProvider(
        IAppSettingsRepository settings, ICredentialStore credentials, ILogger<ProxyProvider> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<ProxyResolution?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetProxyAsync(cancellationToken).ConfigureAwait(false);
        return settings.Mode switch
        {
            ProxyMode.Manual => ResolveManual(settings),
            ProxyMode.System => ResolveSystem(),
            _ => null,
        };
    }

    private ProxyResolution? ResolveManual(ProxySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Url) || !Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        NetworkCredential? credential = null;
        var gitUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            var password = _credentials.Retrieve(CredentialKeys.Proxy()) ?? string.Empty;
            credential = new NetworkCredential(settings.Username, password);
            // Embed URL-encoded credentials for git's http.proxy (passed as a -c arg, never on disk).
            gitUrl = $"{uri.Scheme}://{Uri.EscapeDataString(settings.Username)}:{Uri.EscapeDataString(password)}@{uri.Host}:{uri.Port}";
        }

        var webProxy = new WebProxy(uri) { Credentials = credential };

        // Entries are compiled as REGULAR EXPRESSIONS by WebProxy, not matched as hostnames, and the
        // setter throws on the first one that does not compile. That throw lands on the run path, so
        // a single malformed entry — "*.corp.local", which is what anyone would type — failed every
        // run of every job. Invalid entries are dropped rather than allowed to do that.
        var bypass = new List<string>();
        foreach (var host in settings.BypassHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            try
            {
                _ = new Regex(host);
                bypass.Add(host);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning(
                    "Proxy bypass entry {Entry} is not a valid pattern and was ignored. "
                    + "Bypass entries are regular expressions, so a host like host.example.com is "
                    + "written as host\\.example\\.com and a wildcard as .*\\.example\\.com.",
                    host);
            }
        }

        webProxy.BypassList = [.. bypass];

        // ...and the bypass has to reach GIT, not only HttpClient.
        //
        // BypassList governs WebProxy, which is the HTTP client's proxy — so the Add Repository
        // dialog, the token check and the whole preflight honoured it while every clone, fetch and
        // push still went through the proxy, because git was handed a proxy URL unconditionally.
        // The user saw five green ticks and then a failing run. This mirrors what the System mode
        // already does a few lines below.
        return new ProxyResolution(webProxy, webProxy.IsBypassed(GitHubRemote) ? null : gitUrl);
    }

    private static ProxyResolution? ResolveSystem()
    {
        var system = HttpClient.DefaultProxy;
        var gitUrl = system.IsBypassed(GitHubRemote) ? null : system.GetProxy(GitHubRemote)?.ToString();

        // If the OS proxy applies to neither GitHub endpoint, there is effectively no proxy.
        if (gitUrl is null && system.IsBypassed(GitHubApi))
        {
            return null;
        }

        return new ProxyResolution(system, gitUrl);
    }

    public async Task<Result> TestAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        using var handler = new HttpClientHandler { Proxy = resolution?.WebProxy, UseProxy = resolution is not null };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Obsync");

        try
        {
            using var response = await http.GetAsync(GitHubApi, cancellationToken).ConfigureAwait(false);
            // Any HTTP response (even 401/403) means we reached GitHub through the proxy.
            return (int)response.StatusCode < 500
                ? Result.Success()
                : Result.Failure($"GitHub returned {(int)response.StatusCode} through the proxy.");
        }
        catch (Exception ex)
        {
            return Result.Failure(
                $"Could not reach GitHub {(resolution is null ? "directly" : "through the proxy")} — {ex.Message}");
        }
    }
}
