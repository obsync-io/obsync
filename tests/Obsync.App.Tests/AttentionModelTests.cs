using Obsync.App.ViewModels;
using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The dashboard's "Needs attention" aggregation: failed and warning last runs, overdue schedules,
/// and failed server tests each produce one row with the right severity and corrective action —
/// and a healthy estate produces none (the card must be absent, not an empty shell).
/// </summary>
public sealed class AttentionModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static SyncJob Job(string name, RunStatus? lastStatus = null, Guid? lastRunId = null,
        DateTimeOffset? nextRunAt = null) => new()
    {
        Name = name,
        Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily },
        RunSummary = new JobRunSummary { LastStatus = lastStatus, LastRunId = lastRunId, NextRunAt = nextRunAt },
    };

    [Fact]
    public void Build_ProducesOneRowPerProblem_WithSeverityAndAction()
    {
        var failedRunId = Guid.NewGuid();
        var failed = Job("Broken", RunStatus.Failed, failedRunId);
        var warned = Job("Partial", RunStatus.Warning);
        var overdue = Job("Stalled", RunStatus.Succeeded, nextRunAt: Now.AddHours(-1));
        var healthy = Job("Fine", RunStatus.Succeeded, nextRunAt: Now.AddHours(1));
        var badServer = new SqlConnectionProfile { Name = "PROD-SQL01", LastTestStatus = ConnectionTestStatus.Failed };
        var goodServer = new SqlConnectionProfile { Name = "DEV-SQL01", LastTestStatus = ConnectionTestStatus.Connected };
        var runErrors = new Dictionary<Guid, string> { [failedRunId] = "Login failed for user 'svc'.\nStack trace…" };

        var items = AttentionModel.Build(
            [failed, warned, overdue, healthy], [badServer, goodServer], runErrors, Now);

        Assert.Equal(4, items.Count);

        var failedRow = Assert.Single(items, i => i.Text.Contains("failed —"));
        Assert.Equal(AttentionSeverity.Error, failedRow.Severity);
        Assert.Equal("Job “Broken” failed — Login failed for user 'svc'.", failedRow.Text); // first line only
        Assert.Equal(("Open", failed.Id), (failedRow.ActionLabel, failedRow.JobId));

        var warningRow = Assert.Single(items, i => i.Text.Contains("warnings"));
        Assert.Equal(AttentionSeverity.Warning, warningRow.Severity);
        Assert.Equal(("Open", warned.Id), (warningRow.ActionLabel, warningRow.JobId));

        var overdueRow = Assert.Single(items, i => i.Text.Contains("missed its scheduled run"));
        Assert.Equal(AttentionSeverity.Warning, overdueRow.Severity);
        Assert.Equal(("Open", overdue.Id), (overdueRow.ActionLabel, overdueRow.JobId));

        var serverRow = Assert.Single(items, i => i.Text.Contains("connection test"));
        Assert.Equal(AttentionSeverity.Error, serverRow.Severity);
        Assert.Equal("Server “PROD-SQL01” failed its last connection test", serverRow.Text);
        Assert.Equal(("Open Servers", (Guid?)null), (serverRow.ActionLabel, serverRow.JobId));
    }

    [Fact]
    public void Build_QuotesNoError_WhenTheFailedRunIsOutsideTheRecentWindow()
    {
        var failed = Job("Broken", RunStatus.Failed, Guid.NewGuid());

        var items = AttentionModel.Build([failed], [], new Dictionary<Guid, string>(), Now);

        Assert.Equal("Job “Broken” failed", Assert.Single(items).Text);
    }

    [Fact]
    public void Build_IsEmpty_WhenEverythingIsHealthy()
    {
        var jobs = new[]
        {
            Job("A", RunStatus.Succeeded, nextRunAt: Now.AddHours(2)),
            Job("B", RunStatus.NoChanges),
            Job("C"), // never run
        };
        var servers = new[]
        {
            new SqlConnectionProfile { Name = "S", LastTestStatus = ConnectionTestStatus.Connected },
            new SqlConnectionProfile { Name = "T", LastTestStatus = ConnectionTestStatus.Untested },
        };

        Assert.Empty(AttentionModel.Build(jobs, servers, new Dictionary<Guid, string>(), Now));
    }
}
