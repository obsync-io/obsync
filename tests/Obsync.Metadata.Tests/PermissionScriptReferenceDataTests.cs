using Obsync.Metadata;
using Xunit;

namespace Obsync.Metadata.Tests;

/// <summary>
/// Settings calls this script "exactly what Obsync needs". It was not, for one job shape: reference
/// data versioning reads table CONTENTS, and none of CONNECT / VIEW DEFINITION / VIEW DATABASE STATE
/// permits a SELECT. The wizard's table picker reads sys.tables and partition metadata — both visible
/// under VIEW DEFINITION — so the row counts even looked right, and the run then failed on the first
/// SELECT.
/// </summary>
public sealed class PermissionScriptReferenceDataTests
{
    private static readonly string[] Databases = ["Sales"];

    [Fact]
    public void ByDefault_NoSelectIsGranted()
    {
        // The whole point of this script is least privilege; most jobs never read a row.
        Assert.DoesNotContain("GRANT SELECT", SqlPermissionScriptBuilder.Build("svc", Databases));
    }

    [Fact]
    public void WithReferenceData_SelectIsGranted()
    {
        var script = SqlPermissionScriptBuilder.Build("svc", Databases, includeReferenceData: true);

        Assert.Contains("GRANT SELECT TO", script);
        Assert.Contains("GRANT VIEW DEFINITION TO", script);
    }

    [Fact]
    public void TheRevokeScript_MirrorsTheGrantExactly()
    {
        // An offboarding script that leaves SELECT behind is worse than no script: the account looks
        // revoked and can still read every row.
        Assert.Contains("REVOKE SELECT FROM",
            SqlPermissionScriptBuilder.BuildRevoke("svc", Databases, includeReferenceData: true));
        Assert.DoesNotContain("REVOKE SELECT FROM",
            SqlPermissionScriptBuilder.BuildRevoke("svc", Databases));
    }
}
