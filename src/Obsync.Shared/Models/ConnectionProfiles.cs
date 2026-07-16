namespace Obsync.Shared.Models;

/// <summary>
/// A reusable SQL Server connection. The password for <see cref="SqlAuthenticationMode.SqlLogin"/>
/// is never stored here — it lives in Windows Credential Manager, keyed by <see cref="Id"/>.
/// </summary>
public sealed class SqlConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly name shown in the UI (e.g. "PROD-SQL01").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Server / instance name, e.g. <c>PROD-SQL01</c> or <c>HOST\SQLAG15</c>.</summary>
    public string ServerName { get; set; } = string.Empty;

    public SqlAuthenticationMode AuthenticationMode { get; set; } = SqlAuthenticationMode.WindowsIntegrated;

    /// <summary>Login name, used only for <see cref="SqlAuthenticationMode.SqlLogin"/>.</summary>
    public string? Username { get; set; }

    /// <summary>Encrypt the client/server connection (recommended).</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Trust the server certificate. Disables MITM protection — use only on trusted networks.</summary>
    public bool TrustServerCertificate { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>Outcome of the most recent connectivity test (persisted for at-a-glance health).</summary>
    public ConnectionTestStatus LastTestStatus { get; set; } = ConnectionTestStatus.Untested;

    /// <summary>When the connection was last tested; null if never.</summary>
    public DateTimeOffset? LastTestedAt { get; set; }

    /// <summary>Detail of the last test: the server edition/version on success, or the error on failure.</summary>
    public string? LastTestDetail { get; set; }

    /// <summary>The server edition (e.g. "Enterprise Edition") captured by the last successful test; null when unknown.</summary>
    public string? ServerEdition { get; set; }

    /// <summary>The server product version (e.g. "16.0.4105.2") captured by the last successful test; null when unknown.</summary>
    public string? ServerVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this profile needs a password retrieved from the credential store.</summary>
    public bool RequiresPassword => AuthenticationMode == SqlAuthenticationMode.SqlLogin;

    /// <summary>"{Edition} · {version}" for the Servers grid; empty when never captured.</summary>
    public string ServerProductDisplay => (ServerEdition, ServerVersion) switch
    {
        (null or "", null or "") => string.Empty,
        (null or "", var version) => version!,
        (var edition, null or "") => edition!,
        var (edition, version) => $"{edition} · {version}",
    };
}

/// <summary>The persisted outcome of a repository profile's most recent validation.</summary>
public enum RepositoryValidationStatus
{
    /// <summary>Never validated (or the profile changed since).</summary>
    Unvalidated = 0,

    /// <summary>Token, repository access, write permission, and default branch all checked out.</summary>
    Valid = 1,

    /// <summary>Usable but degraded — e.g. a read-only token that breaks push-based commit modes.</summary>
    Attention = 2,

    /// <summary>Unusable — invalid token, unreachable repository, or missing default branch.</summary>
    Failed = 3,
}

/// <summary>
/// A reusable GitHub repository destination. The access token is never stored here — it
/// lives in Windows Credential Manager, keyed by <see cref="Id"/>.
/// </summary>
public sealed class GitRepositoryProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly name shown in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Repository owner (user or organization), e.g. <c>company</c>.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Repository name, e.g. <c>sql-schema-history</c>.</summary>
    public string RepositoryName { get; set; } = string.Empty;

    /// <summary>HTTPS clone URL. Derived from owner/name when not set explicitly.</summary>
    public string? RemoteUrl { get; set; }

    /// <summary>Default branch to target when a job does not override it.</summary>
    public string DefaultBranch { get; set; } = "main";

    public GitHubAuthMode AuthMode { get; set; } = GitHubAuthMode.PersonalAccessToken;

    /// <summary>Outcome of the most recent validation (persisted for at-a-glance health, like the server test status).</summary>
    public RepositoryValidationStatus LastValidationStatus { get; set; } = RepositoryValidationStatus.Unvalidated;

    /// <summary>When the repository was last validated; null if never.</summary>
    public DateTimeOffset? LastValidatedAt { get; set; }

    /// <summary>Detail of the last validation: the verdict on success, or what went wrong.</summary>
    public string? LastValidationDetail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary><c>owner/name</c>, e.g. <c>company/sql-schema-history</c>.</summary>
    public string FullName => $"{Owner}/{RepositoryName}";

    /// <summary>The effective HTTPS clone URL.</summary>
    public string EffectiveRemoteUrl =>
        string.IsNullOrWhiteSpace(RemoteUrl) ? $"https://github.com/{Owner}/{RepositoryName}.git" : RemoteUrl;
}
