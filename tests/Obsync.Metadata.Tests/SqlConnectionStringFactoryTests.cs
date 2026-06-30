using Microsoft.Data.SqlClient;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Metadata.Tests;

public sealed class SqlConnectionStringFactoryTests
{
    private readonly SqlConnectionStringFactory _factory = new();

    [Fact]
    public void Create_WindowsIntegrated_UsesIntegratedSecurityAndNoCredentials()
    {
        var profile = new SqlConnectionProfile
        {
            ServerName = "PROD-SQL01",
            AuthenticationMode = SqlAuthenticationMode.WindowsIntegrated,
            Encrypt = true,
            TrustServerCertificate = false,
        };

        var builder = new SqlConnectionStringBuilder(_factory.Create(profile, password: null, database: "SalesDB"));

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal("PROD-SQL01", builder.DataSource);
        Assert.Equal("SalesDB", builder.InitialCatalog);
        Assert.Equal("Obsync", builder.ApplicationName);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.Equal(string.Empty, builder.UserID);
    }

    [Fact]
    public void Create_SqlLogin_SetsUserIdPasswordAndOptionalEncryption()
    {
        var profile = new SqlConnectionProfile
        {
            ServerName = "HOST\\SQLAG15",
            AuthenticationMode = SqlAuthenticationMode.SqlLogin,
            Username = "svc_obsync",
            Encrypt = false,
        };

        var builder = new SqlConnectionStringBuilder(_factory.Create(profile, password: "p@ss:w0rd", database: null));

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("svc_obsync", builder.UserID);
        Assert.Equal("p@ss:w0rd", builder.Password);
        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.Equal(string.Empty, builder.InitialCatalog);
    }

    [Fact]
    public void Create_TrustServerCertificate_IsHonored()
    {
        var profile = new SqlConnectionProfile { ServerName = "S", TrustServerCertificate = true };

        var builder = new SqlConnectionStringBuilder(_factory.Create(profile, password: null));

        Assert.True(builder.TrustServerCertificate);
    }
}
