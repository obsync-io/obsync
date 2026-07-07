using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

/// <summary>The security-review markdown: grouping, ordering, summary line, and safe cells.</summary>
public sealed class SecurityReviewWriterTests
{
    [Fact]
    public void Build_GroupsBySeverity_HighFirst_AndOrdersDeterministically()
    {
        var markdown = SecurityReviewWriter.Build("SalesDB",
        [
            new SecurityFinding(SecuritySeverity.Medium, "db_owner member", "app_user", "Member of db_owner."),
            new SecurityFinding(SecuritySeverity.High, "Trustworthy", "SalesDB", "TRUSTWORTHY is ON."),
            new SecurityFinding(SecuritySeverity.Medium, "Grant to public", "dbo.Orders", "GRANT SELECT to public."),
            new SecurityFinding(SecuritySeverity.Info, "Orphaned user", "(check skipped)", "Cannot view logins."),
        ]);

        Assert.StartsWith("# Security review — SalesDB", markdown);
        Assert.Contains("**4 findings:** 1 high, 2 medium, 1 informational.", markdown);

        var high = markdown.IndexOf("## High", StringComparison.Ordinal);
        var medium = markdown.IndexOf("## Medium", StringComparison.Ordinal);
        var info = markdown.IndexOf("## Informational", StringComparison.Ordinal);
        Assert.True(high >= 0 && high < medium && medium < info);

        // Within a severity, findings order by check name.
        Assert.True(markdown.IndexOf("Grant to public", StringComparison.Ordinal)
            < markdown.IndexOf("db_owner member", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithNoFindings_SaysSoInsteadOfEmptyTables()
    {
        var markdown = SecurityReviewWriter.Build("CleanDB", []);

        Assert.Contains("**No findings.**", markdown);
        Assert.DoesNotContain("| Check |", markdown);
    }

    [Fact]
    public void Build_EscapesPipesAndNewlines_AndUsesLfOnly()
    {
        var markdown = SecurityReviewWriter.Build("SalesDB",
        [
            new SecurityFinding(SecuritySeverity.High, "High-risk grant", "user|with|pipes", "Holds CONTROL\non the database."),
        ]);

        Assert.Contains(@"user\|with\|pipes", markdown);
        Assert.Contains("Holds CONTROL on the database.", markdown);
        Assert.DoesNotContain("\r", markdown);
    }

    [Fact]
    public void Summary_UsesSingularForOneFinding() =>
        Assert.Equal(
            "**1 finding:** 1 high.",
            SecurityReviewWriter.Summary([new SecurityFinding(SecuritySeverity.High, "c", "s", "d")]));
}
