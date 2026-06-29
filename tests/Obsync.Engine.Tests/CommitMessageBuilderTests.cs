using Obsync.Engine;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Engine.Tests;

public sealed class CommitMessageBuilderTests
{
    private static (SyncRun Run, SyncJob Job, List<ObjectChange> Changes) Sample()
    {
        var run = new SyncRun
        {
            RunKey = "20260628-230000",
            ServerName = "PROD-SQL01",
            Databases = "SalesDB",
            StartedAt = new DateTimeOffset(2026, 6, 28, 23, 0, 0, TimeSpan.Zero),
            ObjectsScanned = 42120,
            ObjectsAdded = 1,
            ObjectsModified = 2,
            ObjectsDeleted = 1,
            DurationMs = 151_000,
        };
        var job = new SyncJob { Name = "SalesDB Production Sync" };
        var changes = new List<ObjectChange>
        {
            Change(ChangeType.Modified, "procedures/dbo.usp_GetCustomer.sql"),
            Change(ChangeType.Modified, "views/dbo.vw_SalesSummary.sql"),
            Change(ChangeType.Added, "procedures/dbo.usp_NewReport.sql"),
            Change(ChangeType.Deleted, "functions/dbo.fn_OldTax.sql"),
        };
        return (run, job, changes);
    }

    private static ObjectChange Change(ChangeType type, string path) => new()
    {
        ChangeType = type,
        ObjectType = SqlObjectType.StoredProcedure,
        Schema = "dbo",
        Name = Path.GetFileNameWithoutExtension(path),
        RelativePath = path,
    };

    [Fact]
    public void Build_Subject_MatchesSpecifiedFormat()
    {
        var (run, job, changes) = Sample();

        var (subject, _) = CommitMessageBuilder.Build(run, job, changes);

        Assert.StartsWith("[SalesDB] SQL object changes from PROD-SQL01 - 2026-06-28", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Body_IncludesCountsAndCategorizedFiles()
    {
        var (run, job, changes) = Sample();

        var (_, body) = CommitMessageBuilder.Build(run, job, changes);

        Assert.Contains("Server: PROD-SQL01", body, StringComparison.Ordinal);
        Assert.Contains("Database: SalesDB", body, StringComparison.Ordinal);
        Assert.Contains("Job: SalesDB Production Sync", body, StringComparison.Ordinal);
        Assert.Contains("Run ID: 20260628-230000", body, StringComparison.Ordinal);
        Assert.Contains("Objects scanned: 42,120", body, StringComparison.Ordinal);
        Assert.Contains("Added: 1", body, StringComparison.Ordinal);
        Assert.Contains("Modified: 2", body, StringComparison.Ordinal);
        Assert.Contains("Deleted: 1", body, StringComparison.Ordinal);
        Assert.Contains("Duration: 00:02:31", body, StringComparison.Ordinal);

        // Categorized file lists.
        Assert.Contains("Modified:", body, StringComparison.Ordinal);
        Assert.Contains("  - procedures/dbo.usp_GetCustomer.sql", body, StringComparison.Ordinal);
        Assert.Contains("Added:", body, StringComparison.Ordinal);
        Assert.Contains("  - procedures/dbo.usp_NewReport.sql", body, StringComparison.Ordinal);
        Assert.Contains("Deleted:", body, StringComparison.Ordinal);
        Assert.Contains("  - functions/dbo.fn_OldTax.sql", body, StringComparison.Ordinal);
    }
}
