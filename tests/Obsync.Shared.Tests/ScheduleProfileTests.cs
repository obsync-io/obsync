using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Shared.Tests;

public sealed class ScheduleProfileTests
{
    // A fixed reference instant keeps the assertions deterministic regardless of the machine clock.
    private static readonly DateTimeOffset From = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Manual_HasNoNextRun()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Manual };
        Assert.Null(schedule.GetNextRun(From));
    }

    [Fact]
    public void Cron_IsComputedByTheScheduler_NotHere()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = "0 0 3 * * ?" };
        Assert.Null(schedule.GetNextRun(From));
    }

    [Fact]
    public void Daily_ReturnsNextOccurrenceAtConfiguredLocalTime()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(23, 30) };

        var next = schedule.GetNextRun(From);

        Assert.NotNull(next);
        Assert.True(next > From);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(23, local.Hour);
        Assert.Equal(30, local.Minute);
    }

    [Fact]
    public void Weekly_LandsOnConfiguredDayAndTime()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDay = new TimeOnly(9, 0),
        };

        var next = schedule.GetNextRun(From);

        Assert.NotNull(next);
        Assert.True(next > From);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(DayOfWeek.Sunday, local.DayOfWeek);
        Assert.Equal(9, local.Hour);
    }

    [Fact]
    public void Hourly_EveryHour_ReturnsNextTopOfHour()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = 1 };

        var next = schedule.GetNextRun(From);

        Assert.NotNull(next);
        Assert.True(next > From);
        Assert.Equal(0, next!.Value.ToLocalTime().Minute);
    }

    [Fact]
    public void Hourly_WithInterval_AlignsToMultipleOfIntervalFromMidnight()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = 3 };

        var next = schedule.GetNextRun(From);

        Assert.NotNull(next);
        Assert.True(next > From);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(0, local.Minute);
        Assert.Equal(0, local.Hour % 3);
    }
}
