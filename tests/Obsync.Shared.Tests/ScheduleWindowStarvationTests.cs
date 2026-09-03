using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// A schedule whose occurrences never land inside its enabled maintenance window builds a perfectly
/// healthy Quartz trigger and is then skipped by the engine every single time — with one Information
/// log line, no history row, and an advancing "Next run" that keeps the overdue detector quiet.
/// <see cref="ScheduleProfile.MaintenanceWindowConflictReason"/> is the rule that refuses it up front.
///
/// The guard originally covered Daily and Weekly only; Hourly and Cron went unchecked, which is the
/// regression most of these lock down.
/// </summary>
public sealed class ScheduleWindowStarvationTests
{
    private static ScheduleProfile Hourly(int interval, TimeOnly start, TimeOnly end,
        MaintenanceDayScope scope = MaintenanceDayScope.AnyDay) => new()
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = interval,
            MaintenanceWindowEnabled = true,
            WindowStart = start,
            WindowEnd = end,
            DayScope = scope,
        };

    [Fact]
    public void Hourly_IntervalWhoseFiresStraddleTheWindow_IsRefused()
    {
        // Every 7 hours emits `0 0 0/7 * * ?` — the step restarts at midnight, so fires land at
        // 00/07/14/21 and a 01:00-04:00 window admits none of them, ever.
        var schedule = Hourly(7, new TimeOnly(1, 0), new TimeOnly(4, 0));

        var reason = schedule.MaintenanceWindowConflictReason();

        Assert.NotNull(reason);
        Assert.Contains("00:00, 07:00, 14:00, 21:00", reason);
        Assert.Contains("never", reason);
    }

    [Fact]
    public void Hourly_EveryHourWithAMidHourWindow_IsRefused()
    {
        // The window is open 30 minutes a day and looks entirely reasonable, but hourly fires are
        // always at minute :00, so none of the 24 can land in 02:15-02:45.
        var schedule = Hourly(1, new TimeOnly(2, 15), new TimeOnly(2, 45));

        var reason = schedule.MaintenanceWindowConflictReason();

        Assert.NotNull(reason);
        Assert.Contains("on the hour, every hour", reason);
    }

    [Fact]
    public void Hourly_FireInsideTheWindow_IsAllowed()
    {
        // Every 6 hours fires at 00/06/12/18; 00:00 is inside the overnight 22:00-05:00 window.
        Assert.Null(Hourly(6, new TimeOnly(22, 0), new TimeOnly(5, 0)).MaintenanceWindowConflictReason());
    }

    [Fact]
    public void Hourly_EveryHourInsideAnOvernightWindow_IsAllowed()
    {
        // The shape the shipped wizard already saves and must keep saving.
        Assert.Null(Hourly(1, new TimeOnly(22, 0), new TimeOnly(5, 0), MaintenanceDayScope.WeekdaysOnly)
            .MaintenanceWindowConflictReason());
    }

    [Fact]
    public void Hourly_DayScopeIsAttributedToTheDayTheWindowOpened()
    {
        // Every 12 hours fires at 00:00 and 12:00. Under a weekends-only 22:00-05:00 window the only
        // candidate is 00:00, which belongs to the PREVIOUS day's window — so Sunday 00:00 counts as
        // Saturday's window and the schedule is admitted. Same attribution the engine applies.
        Assert.Null(Hourly(12, new TimeOnly(22, 0), new TimeOnly(5, 0), MaintenanceDayScope.WeekendsOnly)
            .MaintenanceWindowConflictReason());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Hourly_NonPositiveInterval_IsTreatedAsEveryHour(int interval)
    {
        // Both the translator and the next-run preview normalize a non-positive interval to "every
        // hour" rather than rejecting it, so this rule has to read it the same way — otherwise it
        // would judge a fire set the job does not actually have.
        var schedule = Hourly(interval, new TimeOnly(2, 15), new TimeOnly(2, 45));

        Assert.Contains("on the hour, every hour", schedule.MaintenanceWindowConflictReason());

        Assert.Null(Hourly(interval, new TimeOnly(22, 0), new TimeOnly(5, 0)).MaintenanceWindowConflictReason());
    }

    [Theory]
    [InlineData(ScheduleKind.Hourly)]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.Weekly)]
    [InlineData(ScheduleKind.Cron)]
    public void ZeroLengthWindow_IsRefusedForEveryScheduledCadence(ScheduleKind kind)
    {
        // Equal bounds take the non-wrapping branch, `time >= 03:00 && time < 03:00`, which is false
        // for all 1440 minutes — so it starves every cadence, including the one this layer otherwise
        // defers to a cron engine.
        var schedule = new ScheduleProfile
        {
            Kind = kind,
            CronExpression = "0 0 3 * * ?",
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(3, 0),
            WindowEnd = new TimeOnly(3, 0),
        };

        var reason = schedule.MaintenanceWindowConflictReason();

        Assert.NotNull(reason);
        Assert.Contains("opens and closes at the same time", reason);
    }

    [Fact]
    public void ZeroLengthWindow_OnAManualSchedule_IsAllowed()
    {
        // Manual has no scheduled occurrences for the window to starve.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Manual,
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(3, 0),
            WindowEnd = new TimeOnly(3, 0),
        };

        Assert.Null(schedule.MaintenanceWindowConflictReason());
    }

    [Fact]
    public void DisabledWindow_IsAlwaysAllowed()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = 7,
            MaintenanceWindowEnabled = false,
            WindowStart = new TimeOnly(1, 0),
            WindowEnd = new TimeOnly(4, 0),
        };

        Assert.Null(schedule.MaintenanceWindowConflictReason());
    }

    [Fact]
    public void Cron_WithANonDegenerateWindow_IsDeferredToTheCallerWithACronEngine()
    {
        // 02:30 daily never reaches a 03:00-05:00 window, but deciding that needs a cron engine this
        // layer deliberately does not reference — ScheduleWindowGuard answers it instead.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Cron,
            CronExpression = "0 30 2 * * ?",
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(3, 0),
            WindowEnd = new TimeOnly(5, 0),
        };

        Assert.Null(schedule.MaintenanceWindowConflictReason());
    }

    // --- Daily / Weekly: the behaviour that already existed, moved here from the wizard ------------

    [Fact]
    public void Daily_TimeOutsideTheWindow_IsRefused()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Daily,
            TimeOfDay = new TimeOnly(12, 0),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
        };

        Assert.Contains("maintenance window", schedule.MaintenanceWindowConflictReason());
    }

    [Fact]
    public void Daily_TimeInsideTheOvernightWindow_IsAllowed()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Daily,
            TimeOfDay = new TimeOnly(23, 30),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
        };

        Assert.Null(schedule.MaintenanceWindowConflictReason());
    }

    [Fact]
    public void Weekly_DayIncompatibleWithTheWindowDayScope_IsRefused()
    {
        // Sunday 23:00 opens a Sunday window, which a weekdays-only scope never admits.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDay = new TimeOnly(23, 0),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekdaysOnly,
        };

        Assert.Contains("never", schedule.MaintenanceWindowConflictReason());
    }

    [Fact]
    public void Weekly_SundayEarlyMorningAttributedToSaturdaysWindow_IsAllowed()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDay = new TimeOnly(2, 0),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekendsOnly,
        };

        Assert.Null(schedule.MaintenanceWindowConflictReason());
    }
}
