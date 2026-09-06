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

    private static readonly IReadOnlyDictionary<Guid, string> NoSkips = new Dictionary<Guid, string>();

    private static SyncJob Job(string name, RunStatus? lastStatus = null, Guid? lastRunId = null,
        DateTimeOffset? nextRunAt = null) => new()
    {
        Name = name,
        Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily },
        RunSummary = new JobRunSummary { LastStatus = lastStatus, LastRunId = lastRunId, NextRunAt = nextRunAt },
    };

    [Fact]
    public void Build_SaysAStarvedScheduleNeverRuns_RatherThanThatItMissedARun()
    {
        // "Missed its scheduled run" sends the user to look for a service fault. A schedule whose
        // maintenance window can never admit it is not late — it is misconfigured, and it will look
        // exactly the same tomorrow. Only the Jobs grid and the job header said so before this.
        var starved = Job("Starved", RunStatus.Succeeded, nextRunAt: Now.AddHours(-1));
        starved.Schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDay = new TimeOnly(23, 0),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekdaysOnly,
        };

        var items = AttentionModel.Build([starved], [], [], new Dictionary<Guid, string>(), NoSkips, Now);

        var row = Assert.Single(items);
        Assert.Equal(AttentionSeverity.Warning, row.Severity);
        Assert.Contains("never runs", row.Text);
        Assert.DoesNotContain("missed its scheduled run", row.Text);
    }

    [Fact]
    public void Build_StillReportsAnOrdinaryOverdueJob()
    {
        // The exclusion must be narrow: a job that can run and has not is still overdue.
        var overdue = Job("Stalled", RunStatus.Succeeded, nextRunAt: Now.AddHours(-1));

        var items = AttentionModel.Build([overdue], [], [], new Dictionary<Guid, string>(), NoSkips, Now);

        Assert.Contains("missed its scheduled run", Assert.Single(items).Text);
    }

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
            [failed, warned, overdue, healthy], [badServer, goodServer], [], runErrors, NoSkips, Now);

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

        var items = AttentionModel.Build([failed], [], [], new Dictionary<Guid, string>(), NoSkips, Now);

        Assert.Equal("Job “Broken” failed", Assert.Single(items).Text);
    }

    /// <summary>
    /// A dropped scheduled occurrence used to be invisible on every surface: no run row, no alert,
    /// and the overdue rule structurally cannot catch it, because reconcile keeps the next-run time
    /// in the future. This row is the signal.
    /// </summary>
    [Fact]
    public void Build_SurfacesASkippedOccurrence_QuotingItsReason()
    {
        // Healthy in every other respect: a real run succeeded, and the next run is comfortably
        // ahead — exactly the state in which the job looked fine while quietly not syncing.
        var job = Job("Nightly", RunStatus.Succeeded, nextRunAt: Now.AddHours(1));
        var skips = new Dictionary<Guid, string>
        {
            [job.Id] = "Another job sharing this repository kept its workspace busy.\nSecond line.",
        };

        var row = Assert.Single(AttentionModel.Build([job], [], [], new Dictionary<Guid, string>(), skips, Now));

        Assert.Equal(AttentionSeverity.Warning, row.Severity);
        Assert.Equal(
            "Job “Nightly” skipped a scheduled run — Another job sharing this repository kept its workspace busy.",
            row.Text); // first line only, like the failed row
        Assert.Equal(("Open", job.Id), (row.ActionLabel, row.JobId));
    }

    /// <summary>A job that skipped and then failed shows both — they are different problems.</summary>
    [Fact]
    public void Build_ReportsASkipAlongsideAnOrdinaryFailure()
    {
        var job = Job("Nightly", RunStatus.Failed, nextRunAt: Now.AddHours(1));
        var skips = new Dictionary<Guid, string> { [job.Id] = "Workspace busy." };

        var items = AttentionModel.Build([job], [], [], new Dictionary<Guid, string>(), skips, Now);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Text.Contains("failed", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Text.Contains("skipped a scheduled run", StringComparison.Ordinal));
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

        Assert.Empty(AttentionModel.Build(jobs, servers, [], new Dictionary<Guid, string>(), NoSkips, Now));
    }
}
