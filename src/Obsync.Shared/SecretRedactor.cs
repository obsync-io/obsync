using System.Text.RegularExpressions;

namespace Obsync.Shared;

/// <summary>
/// Removes credential material from text that outlives the process — persisted run errors, logged
/// command lines, and the support bundle a user attaches to a bug report.
///
/// Obsync never puts a secret in a URL itself: the GitHub token travels as an <c>AUTHORIZATION</c>
/// header injected through <c>GIT_CONFIG_*</c> environment variables. What reaches here is material
/// the operator supplied — a repository profile's free-text <c>RemoteUrl</c>, or a configured proxy —
/// so this is a scrubbing rule for other people's secrets, applied wherever such text is kept.
///
/// Deliberately pattern-based rather than value-based: the code holding the text has no handle on
/// the credential store, and a value-based scrub would miss anything typed by hand.
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>
    /// The text with URL userinfo and GitHub token shapes replaced. Null and blank pass through, so
    /// callers do not need a guard.
    /// </summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var scrubbed = UrlUserInfo().Replace(text, "://***@");
        return GitHubToken().Replace(scrubbed, "***");
    }

    /// <summary>
    /// Everything between the scheme and the <c>@</c> of a URL authority.
    ///
    /// Note it does NOT require the <c>user:password</c> pair the earlier rule did. RFC 3986 userinfo
    /// has no such requirement, and the two shapes that omit it are exactly the ones that were getting
    /// through: <c>https://&lt;token&gt;@host</c>, which is the form GitHub's own documentation produces,
    /// and <c>http://user:@host</c>, which is what this product builds itself when a proxy has a
    /// username but no stored password.
    ///
    /// The character class still stops at <c>/</c>, so an ordinary URL with no credentials and a bare
    /// email address elsewhere in the message are both left alone. An <c>ssh://git@host</c> remote
    /// loses a non-secret username, which is a fair price: host and path survive, so the message
    /// stays diagnostic.
    /// </summary>
    [GeneratedRegex(@"://[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfo();

    /// <summary>
    /// GitHub's documented token shapes, caught wherever they appear rather than only inside a URL —
    /// a token pasted into a field, echoed by a tool, or quoted back in an API error is the same
    /// secret. <c>ghp_</c> classic, <c>gho_</c> OAuth, <c>ghu_</c> user-to-server, <c>ghs_</c>
    /// server-to-server, <c>ghr_</c> refresh: each carries 36+ base62 characters. Fine-grained
    /// <c>github_pat_</c> tokens are longer and contain an underscore, so they get their own arm.
    /// </summary>
    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{20,}|\bgh[pousr]_[A-Za-z0-9]{36,}", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubToken();
}
