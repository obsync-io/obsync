using System.Text;

namespace Obsync.Metadata;

/// <summary>
/// Generates a least-privilege T-SQL script granting a dedicated account exactly the permissions
/// Obsync needs to script a database — and nothing more. Obsync reads metadata and object
/// definitions only; it never modifies the database and never requires sysadmin. The grant list is
/// derived from what the metadata and SMO providers actually query:
/// <list type="bullet">
///   <item><c>CONNECT</c> — open a connection to each database.</item>
///   <item><c>VIEW DEFINITION</c> — read object definitions (<c>sys.sql_modules</c>, SMO scripting).</item>
///   <item><c>VIEW DATABASE STATE</c> — read database metadata used during scripting.</item>
/// </list>
/// Output is deterministic (stable ordering, LF line endings) so a DBA reviewing it sees no churn.
/// </summary>
public static class SqlPermissionScriptBuilder
{
    /// <summary>
    /// Builds the permission script for <paramref name="accountName"/> across the given
    /// <paramref name="databases"/>. The account may be a SQL login or a Windows account
    /// (e.g. <c>DOMAIN\ObsyncSvc</c>). When <paramref name="includeServerObjects"/> is true, a
    /// server-level section (VIEW ANY DEFINITION / VIEW SERVER STATE in master, plus msdb's
    /// SQLAgentReaderRole for Agent-job scripting) precedes the per-database grants.
    /// </summary>
    public static string Build(string accountName, IReadOnlyList<string> databases, bool includeServerObjects = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        var account = accountName.Trim();
        var user = Quote(account);
        var literal = account.Replace("'", "''");

        var builder = new StringBuilder();
        builder.Append(Header(literal));

        if (includeServerObjects)
        {
            AppendServerObjectGrants(builder, user, literal);
        }

        if (databases.Count == 0)
        {
            builder.Append("\n-- Specify at least one database to generate GRANT statements.\n");
            return Normalize(builder);
        }

        // Stable ordering keeps the script identical run-to-run for the same inputs.
        foreach (var database in databases.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var db = Quote(database);
            builder.Append('\n');
            builder.Append("USE ").Append(db).Append(";\n");
            builder.Append("GO\n\n");
            builder.Append("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'").Append(literal).Append("')\n");
            builder.Append("    CREATE USER ").Append(user).Append(" FOR LOGIN ").Append(user).Append(";\n");
            builder.Append("GO\n\n");
            builder.Append("GRANT CONNECT TO ").Append(user).Append(";\n");
            builder.Append("GRANT VIEW DEFINITION TO ").Append(user).Append(";\n");
            builder.Append("GRANT VIEW DATABASE STATE TO ").Append(user).Append(";\n");
            builder.Append("GO\n");
        }

        return Normalize(builder);
    }

    // Server-level object scripting (logins, server roles, credentials, linked servers) reads
    // instance metadata, and SQL Agent jobs/operators/alerts live in msdb — readable via the
    // built-in SQLAgentReaderRole. Emitted before the per-database blocks, in master first.
    private static void AppendServerObjectGrants(StringBuilder builder, string user, string literal)
    {
        builder.Append('\n');
        builder.Append("-- Server-level object scripting (logins, server roles, credentials, linked servers).\n");
        builder.Append("USE [master];\n");
        builder.Append("GO\n\n");
        builder.Append("GRANT VIEW ANY DEFINITION TO ").Append(user).Append(";\n");
        builder.Append("GRANT VIEW SERVER STATE TO ").Append(user).Append(";\n");
        builder.Append("GO\n\n");
        builder.Append("-- SQL Agent job/operator/alert scripting reads msdb; SQLAgentReaderRole grants it.\n");
        builder.Append("USE [msdb];\n");
        builder.Append("GO\n\n");
        builder.Append("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'").Append(literal).Append("')\n");
        builder.Append("    CREATE USER ").Append(user).Append(" FOR LOGIN ").Append(user).Append(";\n");
        builder.Append("GO\n\n");
        builder.Append("ALTER ROLE [SQLAgentReaderRole] ADD MEMBER ").Append(user).Append(";\n");
        builder.Append("GO\n");
    }

    // The header uses a raw string literal, whose newlines follow the source file's encoding.
    // Force LF so the whole script matches the LF-built body and stays byte-stable.
    private static string Normalize(StringBuilder builder) => builder.ToString().Replace("\r\n", "\n");

    private static string Header(string literal) =>
        $"""
        /* -----------------------------------------------------------------------------
           Obsync — least-privilege SQL permissions

           Obsync reads metadata and object definitions only. It never modifies the
           database and never requires sysadmin. This grants the account exactly what
           Obsync needs to script the database(s) below.

           Create the login first (run once, in the master database):

               CREATE LOGIN [{literal}] WITH PASSWORD = '<strong password>';   -- SQL login
               -- or, for a Windows account:
               CREATE LOGIN [{literal}] FROM WINDOWS;

           For a job that spans many databases you may prefer server-wide grants
           (run in master) instead of the per-database grants below:

               GRANT VIEW ANY DEFINITION TO [{literal}];
               GRANT VIEW SERVER STATE   TO [{literal}];

           Generated by Obsync.
           ----------------------------------------------------------------------------- */

        """;

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
