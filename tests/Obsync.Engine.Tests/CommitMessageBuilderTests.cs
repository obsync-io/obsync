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

        var (subject, _) = CommitMessageBuilder.Build(run, job, changes, ["SalesDB"]);

        Assert.StartsWith("[SalesDB] SQL object changes from PROD-SQL01 - 2026-06-28", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Body_IncludesCountsAndCategorizedFiles()
    {
        var (run, job, changes) = Sample();

        var (_, body) = CommitMessageBuilder.Build(run, job, changes, ["SalesDB"]);

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

    [Fact]
    public void Build_Body_ListsTheFiftySmallestPathsPerCategory_ThenSummarizesTheRest()
    {
        var (run, job, _) = Sample();

        // 60 added changes fed in reverse: the body must list the 50 ordinally smallest paths in
        // ascending order (not the first 50 encountered) and summarize the remaining 10.
        var changes = Enumerable.Range(0, 60)
            .Reverse()
            .Select(i => Change(ChangeType.Added, $"procedures/dbo.usp_Proc{i:D3}.sql"))
            .ToList();

        var (_, body) = CommitMessageBuilder.Build(run, job, changes, ["SalesDB"]);

        var expected = "Added:\n"
            + string.Concat(Enumerable.Range(0, 50).Select(i => $"  - procedures/dbo.usp_Proc{i:D3}.sql\n"))
            + "  … and 10 more";
        Assert.Contains(expected, body, StringComparison.Ordinal);
        Assert.DoesNotContain("usp_Proc050", body, StringComparison.Ordinal);
    }

    private static readonly string[] ManyDatabases =
        [.. Enumerable.Range(1, 40).Select(i => $"CustomerPortal{i:D2}")];

    /// <summary>A run whose scope is <paramref name="databases"/>, joined exactly as the engine joins it.</summary>
    private static SyncRun RunWith(IReadOnlyList<string> databases, string serverName = "PROD-SQL01")
    {
        var (run, _, _) = Sample();
        run.ServerName = serverName;
        run.Databases = string.Join(", ", databases);
        return run;
    }

    private static void AssertWithinBudget(string subject) =>
        Assert.True(subject.Length <= 250, $"subject was {subject.Length} chars: {subject}");

    private static void AssertWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(
                    i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]),
                    $"unpaired high surrogate at index {i}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[i]), $"unpaired low surrogate at index {i}");
            }
        }
    }

    [Fact]
    public void Build_Subject_KeepsTheWholeDatabaseList_WhenItFits()
    {
        var (_, job, changes) = Sample();
        string[] databases = ["SalesDB", "HRDB", "FinanceDB"];

        var (subject, _) = CommitMessageBuilder.Build(RunWith(databases), job, changes, databases);

        Assert.StartsWith(
            "[SalesDB, HRDB, FinanceDB] SQL object changes from PROD-SQL01 - ", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Subject_SummarizesTheScope_WhenTheFullListWouldOverflow()
    {
        // The regression: this subject is also the GitHub pull request title, and an untruncated
        // 40-database list pushed it past the limit, so the PR was rejected on every single run.
        var (_, job, changes) = Sample();

        var (subject, _) = CommitMessageBuilder.Build(RunWith(ManyDatabases), job, changes, ManyDatabases);

        AssertWithinBudget(subject);
        Assert.StartsWith("[CustomerPortal01, ", subject, StringComparison.Ordinal);
        // The scope yields; the tail that makes a subject scannable survives whole.
        Assert.Contains(" +37 more] SQL object changes from PROD-SQL01 - ", subject, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(40)]
    [InlineData(400)]
    public void Build_Subject_StaysWithinBudget_ForAnyDatabaseCount(int count)
    {
        var (_, job, changes) = Sample();
        var databases = Enumerable.Range(1, count)
            .Select(i => $"EnterpriseReportingWarehouse{i:D4}")
            .ToArray();

        var (subject, _) = CommitMessageBuilder.Build(RunWith(databases), job, changes, databases);

        AssertWithinBudget(subject);
    }

    [Fact]
    public void Build_Subject_StaysWithinBudget_WhenTheServerNameAloneExhaustsIt()
    {
        var (_, job, changes) = Sample();
        var run = RunWith(ManyDatabases, new string('s', 300));

        var (subject, _) = CommitMessageBuilder.Build(run, job, changes, ManyDatabases);

        AssertWithinBudget(subject);
        Assert.EndsWith("…", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Subject_NeverSplitsASurrogatePair()
    {
        // SQL Server identifiers are nvarchar, so a database name may carry non-BMP characters;
        // truncating between the halves of a pair would emit an invalid string.
        var (_, job, changes) = Sample();
        string[] databases = [string.Concat(Enumerable.Repeat("𝔄", 400))];

        var (subject, _) = CommitMessageBuilder.Build(RunWith(databases), job, changes, databases);

        AssertWithinBudget(subject);
        AssertWellFormedUtf16(subject);
    }

    [Fact]
    public void Build_Body_SummarizesTheDatabaseList_BeyondFifty()
    {
        // The body carries the same list and is exposed to GitHub's (better attested) 65,536 limit.
        var (_, job, changes) = Sample();
        var databases = Enumerable.Range(1, 60).Select(i => $"DB{i:D2}").ToArray();

        var (_, body) = CommitMessageBuilder.Build(RunWith(databases), job, changes, databases);

        Assert.Contains("Database: DB01, DB02, ", body, StringComparison.Ordinal);
        Assert.Contains("DB50 … and 10 more", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DB60", body, StringComparison.Ordinal);
    }
}
