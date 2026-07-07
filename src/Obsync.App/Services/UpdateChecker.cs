using System.Net;
using System.Net.Http;
using System.Text.Json;
using Obsync.Shared;
using Obsync.Shared.Abstractions;

namespace Obsync.App.Services;

/// <summary>Outcome of a single check against the project's GitHub releases.</summary>
/// <param name="IsUpdateAvailable">True when the latest published release is newer than this build.</param>
/// <param name="LatestVersion">The latest release's version (tag with any leading <c>v</c> stripped), when known.</param>
/// <param name="ReleaseUrl">The latest release's page on github.com, when known.</param>
/// <param name="Error">Null on success; a short, user-facing reason when the check could not complete.</param>
public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error);

/// <summary>
/// Checks the project's GitHub releases for a version newer than the running build. Notify-only:
/// the only endpoint ever contacted is the public releases API, and nothing is transmitted beyond
/// the request itself.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>Fetches the latest release and compares it to the running version. Never throws —
    /// failures come back in <see cref="UpdateCheckResult.Error"/>.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IUpdateChecker" />
public sealed class UpdateChecker : IUpdateChecker
{
    // Set to the real repository at public launch (same placeholder as packaging/Obsync.wxs).
    private const string Owner = "obsync";
    private const string Repo = "obsync";

    /// <summary>Hard cap so a slow or blackholed connection never stalls the caller.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly IProxyProvider _proxy;

    public UpdateChecker(IProxyProvider proxy) => _proxy = proxy;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionInfo.Of(typeof(UpdateChecker).Assembly);
        try
        {
            // Honor the configured proxy exactly like the other outbound HTTP paths.
            var resolution = await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false);
            using var handler = new HttpClientHandler { Proxy = resolution?.WebProxy, UseProxy = resolution is not null };
            using var http = new HttpClient(handler) { Timeout = CheckTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Obsync/{currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Failure("No published releases yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failure($"GitHub responded with {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var tag = release.RootElement.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
            var url = release.RootElement.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null;
            if (TryParseVersion(tag) is not { } latest)
            {
                return Failure("The latest release has no recognizable version tag.");
            }

            return new UpdateCheckResult(IsNewer(tag!, currentVersion), latest.ToString(), url, Error: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("GitHub did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return Failure(ex.Message);
        }
        catch (JsonException)
        {
            return Failure("GitHub returned an unexpected response.");
        }

        static UpdateCheckResult Failure(string reason) => new(false, null, null, reason);
    }

    /// <summary>
    /// Parses a release tag or version string: a leading <c>v</c>/<c>V</c> is stripped and 2–4
    /// dotted numeric components are accepted. Anything else (including prerelease suffixes like
    /// <c>1.2.3-beta</c>) is null — an unrecognizable tag is never treated as an update.
    /// </summary>
    public static Version? TryParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// True when <paramref name="latestTag"/> is a strictly newer version than
    /// <paramref name="currentVersion"/>. Missing components count as zero (so <c>v1.2</c> equals
    /// <c>1.2.0</c>), and either side being unparseable means not-newer.
    /// </summary>
    public static bool IsNewer(string? latestTag, string? currentVersion) =>
        TryParseVersion(latestTag) is { } latest
        && TryParseVersion(currentVersion) is { } current
        && Normalize(latest) > Normalize(current);

    // Version treats an absent component as -1, which would make 1.2.0 "newer" than v1.2.
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
