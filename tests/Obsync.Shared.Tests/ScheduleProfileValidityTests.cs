using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Shared.Tests;

/// <summary>
/// The shared schedulability rule. Its whole purpose is that the wizard, the job-config importer,
/// and the service agree about what can be scheduled — an earlier disagreement let an hourly
/// interval of 24+ be saved as enabled, show a confident next-run time, and never fire.
/// </summary>
public sealed class ScheduleProfileValidityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(23)]
    public void Hourly_WithinRange_IsSchedulable(int interval)
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = interval };

        Assert.Null(schedule.UnschedulableReason());
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(48)]
    [InlineData(99)]
    public void Hourly_BeyondTwentyThree_IsRejected(int interval)
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = interval };

        var reason = schedule.UnschedulableReason();

        Assert.NotNull(reason);
        // The message has to point somewhere: rejecting without naming the alternative leaves a
        // user who wants "every two days" with nowhere to go.
        Assert.Contains("Daily", reason, StringComparison.Ordinal);
        Assert.Contains("Cron", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hourly_AtOrBelowZero_IsAccepted_AndMeansEveryHour()
    {
        // Zero and negatives are normalized to "every hour" by both the translator and the preview,
        // so they are not unschedulable — only values above the cron hour-step ceiling are.
        Assert.Null(new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = 0 }.UnschedulableReason());
        Assert.Null(new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = -5 }.UnschedulableReason());
    }

    [Theory]
    [InlineData(ScheduleKind.Manual)]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.Weekly)]
    public void OtherCadences_IgnoreTheHourlyInterval(ScheduleKind kind)
    {
        // IntervalHours is carried on every profile but only means anything for Hourly; a stale
        // value left behind by switching cadence must not block an otherwise valid schedule.
        var schedule = new ScheduleProfile { Kind = kind, IntervalHours = 999 };

        Assert.Null(schedule.UnschedulableReason());
    }
}
