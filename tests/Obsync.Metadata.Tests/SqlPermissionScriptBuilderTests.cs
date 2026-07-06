using Obsync.Metadata;
using Xunit;

namespace Obsync.Metadata.Tests;

public sealed class SqlPermissionScriptBuilderTests
{
    [Fact]
    public void Build_GrantsTheLeastPrivilegeSetPerDatabase()
    {
        var script = SqlPermissionScriptBuilder.Build("svc_obsync", ["SalesDB"]);

        Assert.Contains("USE [SalesDB];", script);
        Assert.Contains("CREATE USER [svc_obsync] FOR LOGIN [svc_obsync];", script);
        Assert.Contains("GRANT CONNECT TO [svc_obsync];", script);
        Assert.Contains("GRANT VIEW DEFINITION TO [svc_obsync];", script);
        Assert.Contains("GRANT VIEW DATABASE STATE TO [svc_obsync];", script);
        // Never asks for more than it needs: the only rights granted to the account are the three
        // least-privilege ones above (no CONTROL / ALTER / elevated grants).
        Assert.DoesNotContain("GRANT CONTROL", script);
        Assert.DoesNotContain("GRANT ALTER", script);
    }

    [Fact]
    public void Build_IsDeterministic_AndOrderIndependent()
    {
        var a = SqlPermissionScriptBuilder.Build("svc", ["SalesDB", "HRDB"]);
        var b = SqlPermissionScriptBuilder.Build("svc", ["HRDB", "SalesDB"]);

        Assert.Equal(a, b);                       // input order does not change output
        Assert.True(a.IndexOf("[HRDB]", StringComparison.Ordinal) < a.IndexOf("[SalesDB]", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_UsesLfLineEndingsOnly()
    {
        var script = SqlPermissionScriptBuilder.Build("svc", ["SalesDB"]);

        Assert.DoesNotContain("\r\n", script);
    }

    [Fact]
    public void Build_EscapesBracketsInTheAccountName()
    {
        var script = SqlPermissionScriptBuilder.Build("weird]name", ["DB"]);

        Assert.Contains("[weird]]name]", script);
    }

    [Fact]
    public void Build_DeduplicatesDatabases()
    {
        var script = SqlPermissionScriptBuilder.Build("svc", ["SalesDB", "salesdb", "SalesDB"]);

        // Case-insensitive dedupe: exactly one USE block.
        var occurrences = script.Split("USE [", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Build_WithNoDatabases_ExplainsWhatToDo()
    {
        var script = SqlPermissionScriptBuilder.Build("svc", []);

        Assert.Contains("Specify at least one database", script);
        Assert.DoesNotContain("USE [", script); // no per-database grant block was emitted
    }

    [Fact]
    public void Build_WithBlankAccount_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlPermissionScriptBuilder.Build("  ", ["DB"]));
    }

    [Fact]
    public void Build_WithServerObjects_AddsTheServerSectionBeforeThePerDatabaseBlocks()
    {
        var script = SqlPermissionScriptBuilder.Build("svc_obsync", ["SalesDB"], includeServerObjects: true);

        // The executable statements (the header only mentions the server-wide grants in a comment).
        Assert.Contains("USE [master];\nGO\n\nGRANT VIEW ANY DEFINITION TO [svc_obsync];\nGRANT VIEW SERVER STATE TO [svc_obsync];", script);
        Assert.Contains("USE [msdb];", script);
        Assert.Contains("ALTER ROLE [SQLAgentReaderRole] ADD MEMBER [svc_obsync];", script);
        // Server grants come first: master before the SalesDB block.
        Assert.True(script.IndexOf("USE [master];", StringComparison.Ordinal)
            < script.IndexOf("USE [SalesDB];", StringComparison.Ordinal));
        // Still no elevated grants.
        Assert.DoesNotContain("GRANT CONTROL", script);
        Assert.DoesNotContain("GRANT ALTER", script);
    }

    [Fact]
    public void Build_WithoutServerObjects_OmitsTheServerSection()
    {
        var script = SqlPermissionScriptBuilder.Build("svc_obsync", ["SalesDB"]);

        Assert.DoesNotContain("USE [master];", script);
        Assert.DoesNotContain("USE [msdb];", script);
        Assert.DoesNotContain("SQLAgentReaderRole", script);
    }
}
