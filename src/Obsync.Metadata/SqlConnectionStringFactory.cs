using Microsoft.Data.SqlClient;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Metadata;

/// <summary>Builds SQL Server connection strings from a connection profile and (optional) password.</summary>
public interface ISqlConnectionStringFactory
{
    string Create(SqlConnectionProfile profile, string? password, string? database = null);
}

/// <inheritdoc cref="ISqlConnectionStringFactory" />
public sealed class SqlConnectionStringFactory : ISqlConnectionStringFactory
{
    public string Create(SqlConnectionProfile profile, string? password, string? database = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.ServerName,
            ApplicationName = "Obsync",
            ConnectTimeout = profile.ConnectTimeoutSeconds,
            Encrypt = profile.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = profile.TrustServerCertificate,
            Pooling = true,
        };

        if (!string.IsNullOrEmpty(database))
        {
            builder.InitialCatalog = database;
        }

        if (profile.AuthenticationMode == SqlAuthenticationMode.SqlLogin)
        {
            builder.UserID = profile.Username ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }
}
