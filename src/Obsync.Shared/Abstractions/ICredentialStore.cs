namespace Obsync.Shared.Abstractions;

/// <summary>
/// Securely stores and retrieves secrets (SQL passwords, GitHub tokens) keyed by a stable
/// reference. Implemented with Windows Credential Manager in <c>Obsync.Security</c>.
/// Secrets are never written to the local state database or logs.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Stores (or replaces) the secret for <paramref name="key"/>.</summary>
    void Store(string key, string secret);

    /// <summary>Retrieves the secret for <paramref name="key"/>, or null when absent.</summary>
    string? Retrieve(string key);

    /// <summary>Removes the secret for <paramref name="key"/> if present.</summary>
    void Delete(string key);

    /// <summary>True when a secret exists for <paramref name="key"/>.</summary>
    bool Exists(string key);

    /// <summary>
    /// Every key this store holds that begins with <paramref name="keyPrefix"/>.
    /// </summary>
    /// <remarks>
    /// Needed to find ORPHANS — secrets whose profile has been deleted. Obsync's keys embed the
    /// profile's GUID, so once the row is gone nothing left in the product can name the secret, and
    /// a live GitHub token with Contents:write could sit in a vault indefinitely with no surface
    /// able to list or remove it. That is worse in the case the product itself creates: it tells
    /// users to store the same secret under the service account too, and deleting the profile in
    /// the app only ever removes the copy in the signed-in user's vault.
    /// <para>
    /// Defaulted to empty rather than abstract so the in-memory fakes used throughout the tests do
    /// not all have to implement enumeration they have no use for. The Windows store overrides it.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> Enumerate(string keyPrefix) => [];
}

/// <summary>Builds the stable Credential Manager keys Obsync uses for each secret kind.</summary>
public static class CredentialKeys
{
    private const string Prefix = "Obsync";

    /// <summary>Key for the SQL login password of a connection profile.</summary>
    public static string SqlPassword(Guid connectionProfileId) =>
        $"{Prefix}:Sql:{connectionProfileId:N}";

    /// <summary>Key for the GitHub personal access token of a repository profile.</summary>
    public static string GitHubToken(Guid repositoryProfileId) =>
        $"{Prefix}:GitHub:{repositoryProfileId:N}";

    /// <summary>Key for the single global authenticated-proxy password.</summary>
    public static string Proxy() => $"{Prefix}:Proxy";

    /// <summary>Key for the single global SMTP password used for email alerts.</summary>
    public static string SmtpPassword() => $"{Prefix}:Smtp";
}
