using Obsync.Engine;
using Xunit;

namespace Obsync.Engine.Tests;

public sealed class HeadBranchNameTests
{
    private static readonly Guid JobId = Guid.Parse("3f2a1b9c-0000-0000-0000-000000000000");

    [Theory]
    [InlineData("SalesDB Sync", "obsync/salesdb-sync/3f2a1b9c")]
    [InlineData("  Prod / Finance!!  ", "obsync/prod-finance/3f2a1b9c")]
    [InlineData("A___B", "obsync/a-b/3f2a1b9c")]        // collapse separator runs to one dash
    [InlineData("", "obsync/job/3f2a1b9c")]              // empty job name falls back to "job"
    [InlineData("***", "obsync/job/3f2a1b9c")]           // all-separator name falls back to "job"
    public void HeadBranchName_ProducesARefSafeSlug(string jobName, string expected)
    {
        Assert.Equal(expected, SyncEngine.HeadBranchName(jobName, JobId));
    }

    [Fact]
    public void HeadBranchName_IsStableAcrossRuns()
    {
        // The whole point of the change: it used to carry the run key, so every run cut a branch
        // nothing deletes and opened a pull request nothing closes. Nothing about a RUN may appear
        // in this name.
        Assert.Equal(
            SyncEngine.HeadBranchName("SalesDB Sync", JobId),
            SyncEngine.HeadBranchName("SalesDB Sync", JobId));
    }

    [Fact]
    public void HeadBranchName_DistinguishesJobsWhoseNamesSlugifyIdentically()
    {
        // "Sales DB" and "sales-db" slugify to the same thing. Sharing one head branch would mean
        // each job's run overwrote the other's open proposal.
        Assert.NotEqual(
            SyncEngine.HeadBranchName("Sales DB", Guid.NewGuid()),
            SyncEngine.HeadBranchName("sales-db", Guid.NewGuid()));
    }
}
