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
