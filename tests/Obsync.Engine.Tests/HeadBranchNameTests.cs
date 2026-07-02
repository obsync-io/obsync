using Obsync.Engine;
using Xunit;

namespace Obsync.Engine.Tests;

public sealed class HeadBranchNameTests
{
    [Theory]
    [InlineData("SalesDB Sync", "20260702-230000", "obsync/salesdb-sync/20260702-230000")]
    [InlineData("  Prod / Finance!!  ", "K", "obsync/prod-finance/K")]
    [InlineData("A___B", "K", "obsync/a-b/K")]        // collapse separator runs to one dash
    [InlineData("", "K", "obsync/job/K")]              // empty job name falls back to "job"
    [InlineData("***", "K", "obsync/job/K")]           // all-separator name falls back to "job"
    public void HeadBranchName_ProducesARefSafeSlug(string jobName, string runKey, string expected)
    {
        Assert.Equal(expected, SyncEngine.HeadBranchName(jobName, runKey));
    }

    [Fact]
    public void HeadBranchName_IsDeterministic()
    {
        Assert.Equal(
            SyncEngine.HeadBranchName("SalesDB Sync", "20260702-230000"),
            SyncEngine.HeadBranchName("SalesDB Sync", "20260702-230000"));
    }
}
