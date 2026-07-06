using Obsync.Engine.Alerting;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine.Tests;

public sealed class RunAlertPayloadTests
{
    private static SyncRun FailedRun() => new()
    {
        JobId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
        JobName = "SalesDB Production Sync",
        RunKey = "20260628-230000",
        Trigger = RunTrigger.Scheduled,
        Status = RunStatus.Failed,
        ServerName = "PROD-SQL01",
        Databases = "SalesDB",
        StartedAt = new DateTimeOffset(2026, 6, 28, 23, 0, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 6, 28, 23, 2, 31, TimeSpan.Zero),
        DurationMs = 151_000,
        ObjectsScanned = 42120,
        ObjectsAdded = 1,
        ObjectsModified = 2,
        ObjectsDeleted = 1,
        ObjectsFailed = 3,
        ErrorMessage = "Could not reach GitHub.\nSecond line detail.",
        Tags = ["prod", "sales"],
    };

    private static SyncRun ChangesRun()
    {
        var run = FailedRun();
        run.Status = RunStatus.Succeeded;
        run.ErrorMessage = null;
        run.ObjectsFailed = 0;
        run.CommitSha = "abc1234def";
        run.CommitUrl = "https://github.com/corp/sql-objects/commit/abc1234def";
        run.PullRequestUrl = "https://github.com/corp/sql-objects/pull/7";
        return run;
    }

    [Theory]
    [InlineData(RunStatus.Failed, "run.failed")]
    [InlineData(RunStatus.Warning, "run.warning")]
    [InlineData(RunStatus.Succeeded, "run.changes")]
    public void Event_MapsStatusToDiscriminator(RunStatus status, string expected)
    {
        var run = FailedRun();
        run.Status = status;

        Assert.Equal(expected, RunAlertPayload.Event(run));
    }

    [Theory]
    [InlineData(RunStatus.Failed, "[Obsync] Run failed — SalesDB Production Sync")]
    [InlineData(RunStatus.Warning, "[Obsync] Run finished with warnings — SalesDB Production Sync")]
    [InlineData(RunStatus.Succeeded, "[Obsync] Changes committed — SalesDB Production Sync")]
    public void BuildEmailSubject_MatchesSpecifiedFormat(RunStatus status, string expected)
    {
        var run = FailedRun();
        run.Status = status;

        Assert.Equal(expected, RunAlertPayload.BuildEmailSubject(run));
    }

    [Fact]
    public void BuildEmailBody_FailedRun_IsExact()
    {
        var expected = string.Join(Environment.NewLine,
        [
            "Job:          SalesDB Production Sync",
            "Server:       PROD-SQL01",
            "Databases:    SalesDB",
            "Status:       Failed",
            "Objects:      1 added, 2 modified, 1 deleted (42,120 scanned, 3 skipped)",
            "Started:      2026-06-28 23:00:00 UTC",
            "Duration:     2m 31s",
            "Error:        Could not reach GitHub.",
            "Tags:         prod, sales",
            string.Empty,
        ]);

        Assert.Equal(expected, RunAlertPayload.BuildEmailBody(FailedRun()));
    }

    [Fact]
    public void BuildEmailBody_ChangesRun_IncludesUrlsAndOmitsError()
    {
        var expected = string.Join(Environment.NewLine,
        [
            "Job:          SalesDB Production Sync",
            "Server:       PROD-SQL01",
            "Databases:    SalesDB",
            "Status:       Succeeded",
            "Objects:      1 added, 2 modified, 1 deleted (42,120 scanned, 0 skipped)",
            "Started:      2026-06-28 23:00:00 UTC",
            "Duration:     2m 31s",
            "Commit:       https://github.com/corp/sql-objects/commit/abc1234def",
            "Pull request: https://github.com/corp/sql-objects/pull/7",
            "Tags:         prod, sales",
            string.Empty,
        ]);

        Assert.Equal(expected, RunAlertPayload.BuildEmailBody(ChangesRun()));
    }

    [Fact]
    public void BuildWebhookJson_FailedRun_IsExact()
    {
        var expected =
            @"{""event"":""run.failed"",""job"":""SalesDB Production Sync"",""jobId"":""6f9619ff-8b86-d011-b42d-00c04fc964ff""," +
            @"""status"":""Failed"",""server"":""PROD-SQL01"",""databases"":""SalesDB"",""started"":""2026-06-28T23:00:00+00:00""," +
            @"""completed"":""2026-06-28T23:02:31+00:00"",""durationMs"":151000," +
            @"""counts"":{""scanned"":42120,""added"":1,""modified"":2,""deleted"":1,""failed"":3},""changeCount"":4," +
            @"""commitUrl"":null,""pullRequestUrl"":null,""error"":""Could not reach GitHub.\nSecond line detail.""," +
            @"""tags"":[""prod"",""sales""]}";

        Assert.Equal(expected, RunAlertPayload.BuildWebhookJson(FailedRun()));
    }

    [Fact]
    public void BuildWebhookJson_ChangesRun_CarriesUrlsAndNullError()
    {
        var json = RunAlertPayload.BuildWebhookJson(ChangesRun());

        Assert.StartsWith(@"{""event"":""run.changes"",""job"":""SalesDB Production Sync"",", json, StringComparison.Ordinal);
        Assert.Contains(@"""commitUrl"":""https://github.com/corp/sql-objects/commit/abc1234def""", json, StringComparison.Ordinal);
        Assert.Contains(@"""pullRequestUrl"":""https://github.com/corp/sql-objects/pull/7""", json, StringComparison.Ordinal);
        Assert.Contains(@"""error"":null", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45_000, "45s")]
    [InlineData(151_000, "2m 31s")]
    [InlineData(3_900_000, "1h 05m")]
    public void FormatDuration_IsCoarseAndReadable(long durationMs, string expected) =>
        Assert.Equal(expected, RunAlertPayload.FormatDuration(durationMs));
}
