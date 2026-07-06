using Obsync.Shared.Objects;
using Obsync.Smo;

namespace Obsync.Engine.Tests;

/// <summary>
/// The pure parts of the server-level SMO provider: the object filter and the guarantee that the
/// provider's collection map covers exactly the catalog's server-scoped band (a new catalog entry
/// without a matching collection would silently script nothing).
/// </summary>
public sealed class ServerObjectScriptingTests
{
    [Fact]
    public void SupportedTypes_CoverExactlyTheCatalogServerBand()
    {
        var catalogServerTypes = SqlObjectTypeCatalog.All
            .Where(d => d.IsServerScoped)
            .Select(d => d.Type)
            .ToHashSet();

        Assert.Equal(catalogServerTypes, SmoServerScriptProvider.SupportedTypes.ToHashSet());
    }

    [Theory]
    [InlineData("svc_reporting", true)]
    [InlineData("DOMAIN\\ObsyncSvc", true)]
    [InlineData("##MS_PolicyEventProcessingLogin##", false)] // system certificate login
    [InlineData("##MS_AgentSigningCertificate##", false)]
    public void ShouldScriptServerObject_FiltersSystemCertificateLogins(string name, bool expected)
    {
        Assert.Equal(expected, SmoServerScriptProvider.ShouldScriptServerObject(
            SqlObjectType.ServerLogin, name, isFixedRole: false));
    }

    [Theory]
    [InlineData("deploy_admins", false, true)] // user-defined role
    [InlineData("sysadmin", true, false)]      // fixed role
    [InlineData("public", false, false)]       // public is not IsFixedRole but is never scripted
    public void ShouldScriptServerObject_FiltersFixedAndPublicServerRoles(string name, bool isFixedRole, bool expected)
    {
        Assert.Equal(expected, SmoServerScriptProvider.ShouldScriptServerObject(
            SqlObjectType.ServerRole, name, isFixedRole));
    }

    [Theory]
    [InlineData(SqlObjectType.LinkedServer)]
    [InlineData(SqlObjectType.ServerCredential)]
    [InlineData(SqlObjectType.AgentJob)]
    [InlineData(SqlObjectType.AgentOperator)]
    [InlineData(SqlObjectType.AgentAlert)]
    public void ShouldScriptServerObject_ScriptsEveryOtherServerType(SqlObjectType type)
    {
        Assert.True(SmoServerScriptProvider.ShouldScriptServerObject(type, "##anything", isFixedRole: false));
    }
}
