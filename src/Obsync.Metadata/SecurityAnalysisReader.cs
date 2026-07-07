using Microsoft.Data.SqlClient;
using Obsync.Shared.Models;
using Obsync.Shared.Scripting;

namespace Obsync.Metadata;

/// <summary>
/// Runs the curated, read-only security checks behind the <c>security-review.md</c> artifacts.
/// Each scope is one round trip of catalog queries; every check is a well-known DBA/audit heuristic
/// with a concrete "why it matters" in the finding text.
/// </summary>
public sealed class SecurityAnalysisReader : ISecurityAnalysisReader
{
    private readonly ISqlConnectionStringFactory _connectionStrings;

    public SecurityAnalysisReader(ISqlConnectionStringFactory connectionStrings) =>
        _connectionStrings = connectionStrings;

    private const string DatabaseQuery =
        """
        SELECT is_trustworthy_on, is_db_chaining_on FROM sys.databases WHERE database_id = DB_ID();

        SELECT COUNT(*) FROM sys.database_permissions p
        JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
        WHERE u.name = 'guest' AND p.permission_name = 'CONNECT' AND p.state IN ('G', 'W');

        SELECT p.state_desc, p.permission_name,
               CASE p.class
                   WHEN 0 THEN 'the database'
                   WHEN 1 THEN QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + '.' + QUOTENAME(OBJECT_NAME(p.major_id))
                   WHEN 3 THEN 'schema ' + QUOTENAME(SCHEMA_NAME(p.major_id))
               END AS target
        FROM sys.database_permissions p
        JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
        WHERE g.name = 'public' AND p.state IN ('G', 'W')
          AND (p.class = 0
               OR p.class = 3
               OR (p.class = 1 AND EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id = p.major_id AND o.is_ms_shipped = 0)))
        ORDER BY 3, 2;

        SELECT g.name, p.permission_name,
               CASE p.class
                   WHEN 0 THEN 'the database'
                   WHEN 1 THEN QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + '.' + QUOTENAME(OBJECT_NAME(p.major_id))
                   WHEN 3 THEN 'schema ' + QUOTENAME(SCHEMA_NAME(p.major_id))
                   ELSE p.class_desc
               END AS target
        FROM sys.database_permissions p
        JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
        WHERE p.state IN ('G', 'W') AND g.principal_id > 4
          AND (p.permission_name IN ('CONTROL', 'TAKE OWNERSHIP', 'IMPERSONATE') OR p.permission_name LIKE 'ALTER ANY%')
        ORDER BY g.name, p.permission_name, target;

        SELECT m.name FROM sys.database_role_members rm
        JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
        WHERE r.name = 'db_owner' AND m.name <> 'dbo'
        ORDER BY m.name;

        SELECT CASE WHEN EXISTS (
            SELECT 1 FROM sys.fn_my_permissions(NULL, 'SERVER')
            WHERE permission_name IN ('VIEW ANY DEFINITION', 'CONTROL SERVER')
        ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;

        SELECT dp.name FROM sys.database_principals dp
        LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid
        WHERE dp.type IN ('S', 'U', 'G') AND dp.principal_id > 4
          AND dp.authentication_type_desc = 'INSTANCE' AND sp.sid IS NULL
        ORDER BY dp.name;
        """;

    private const string ServerQuery =
        """
        SELECT m.name FROM sys.server_role_members rm
        JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.server_principals m ON m.principal_id = rm.member_principal_id
        WHERE r.name = 'sysadmin' AND m.name NOT LIKE 'NT SERVICE\%' AND m.name <> 'sa'
        ORDER BY m.name;

        SELECT name, is_disabled FROM sys.server_principals WHERE sid = 0x01;

        SELECT name FROM sys.sql_logins
        WHERE is_policy_checked = 0 AND is_disabled = 0
        ORDER BY name;

        SELECT pr.name, p.permission_name FROM sys.server_permissions p
        JOIN sys.server_principals pr ON pr.principal_id = p.grantee_principal_id
        WHERE p.state IN ('G', 'W') AND pr.name NOT LIKE '##%' AND pr.sid <> 0x01
          AND p.permission_name IN ('CONTROL SERVER', 'IMPERSONATE ANY LOGIN', 'ALTER ANY LOGIN', 'UNSAFE ASSEMBLY')
        ORDER BY pr.name, p.permission_name;
        """;

    public async Task<IReadOnlyList<SecurityFinding>> ReadDatabaseFindingsAsync(
        SqlConnectionProfile profile, string? password, string database, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        var findings = new List<SecurityFinding>();

        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseQuery;
        command.CommandTimeout = commandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // Database options.
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetBoolean(0))
            {
                findings.Add(new SecurityFinding(SecuritySeverity.High, "Trustworthy", database,
                    "TRUSTWORTHY is ON — modules can escalate to server-level rights via impersonation. Turn it OFF unless strictly required."));
            }

            if (reader.GetBoolean(1))
            {
                findings.Add(new SecurityFinding(SecuritySeverity.Medium, "Cross-database chaining", database,
                    "DB_CHAINING is ON — ownership chains cross database boundaries, bypassing per-database permission checks."));
            }
        }

        // Guest access.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt32(0) > 0)
        {
            findings.Add(new SecurityFinding(SecuritySeverity.High, "Guest access", "guest",
                "The guest user can CONNECT — any server login can enter this database without an explicit user. REVOKE CONNECT FROM guest."));
        }

        // Grants to public.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.Medium, "Grant to public",
                reader.GetString(2),
                $"{reader.GetString(0)} {reader.GetString(1)} is granted to public — every user in the database inherits it."));
        }

        // High-risk grants.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.High, "High-risk grant",
                reader.GetString(0),
                $"Holds {reader.GetString(1)} on {reader.GetString(2)} — this permission allows taking over or impersonating other principals."));
        }

        // db_owner members.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.Medium, "db_owner member",
                reader.GetString(0),
                "Member of db_owner — full control of the database. Review whether a narrower role suffices."));
        }

        // Orphaned users — only trustworthy when the scanning account can actually see server logins.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var canSeeLogins = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetBoolean(0);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        if (canSeeLogins)
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings.Add(new SecurityFinding(SecuritySeverity.Medium, "Orphaned user",
                    reader.GetString(0),
                    "No matching server login — a leftover from a restore or a dropped login. Remap or drop the user."));
            }
        }
        else
        {
            findings.Add(new SecurityFinding(SecuritySeverity.Info, "Orphaned user",
                "(check skipped)",
                "The scanning account cannot view server logins (needs VIEW ANY DEFINITION), so orphaned users cannot be detected reliably."));
        }

        return findings;
    }

    public async Task<IReadOnlyList<SecurityFinding>> ReadServerFindingsAsync(
        SqlConnectionProfile profile, string? password, int commandTimeoutSeconds,
        int lockTimeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        var findings = new List<SecurityFinding>();

        await using var connection = new SqlConnection(_connectionStrings.Create(profile, password));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlSession.ApplyLockTimeoutAsync(connection, lockTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = ServerQuery;
        command.CommandTimeout = commandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // sysadmin members.
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.Medium, "sysadmin member",
                reader.GetString(0),
                "Member of the sysadmin fixed server role — unrestricted control of the instance. Review whether it is still needed."));
        }

        // The sa login (sid 0x01), whatever it has been renamed to.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.GetBoolean(1))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.High, "sa login enabled",
                reader.GetString(0),
                "The built-in sa login is enabled — a permanently privileged, frequently attacked account. Disable it and use named admin logins."));
        }

        // SQL logins without password policy.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.Medium, "Password policy off",
                reader.GetString(0),
                "SQL login with CHECK_POLICY = OFF — Windows password complexity and lockout rules do not apply."));
        }

        // High-risk server-level grants.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new SecurityFinding(SecuritySeverity.High, "High-risk server grant",
                reader.GetString(0),
                $"Holds {reader.GetString(1)} — effectively administrative control of the instance."));
        }

        return findings;
    }
}
