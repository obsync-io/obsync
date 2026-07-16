using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.Shared.Tests;

public sealed class ScheduleWindowTests
{
    // Anchored calendar dates (2024-01-05 is a Friday).
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Disabled_IsAlwaysWithin()
    {
        var schedule = new ScheduleProfile { MaintenanceWindowEnabled = false };
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 3)));
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 14)));
    }

    [Fact]
    public void SameDayWindow_ChecksTimeRange()
    {
        var schedule = new ScheduleProfile
        {
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(9, 0),
            WindowEnd = new TimeOnly(17, 0),
        };
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 12)));
        Assert.False(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 8)));
        Assert.False(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 17))); // end is exclusive
    }

    [Fact]
    public void OvernightWindow_WrapsMidnight()
    {
        var schedule = new ScheduleProfile
        {
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
        };
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 23)));  // late evening
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 6, 2)));   // early morning
        Assert.False(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 12))); // midday
    }

    [Fact]
    public void WeekdaysOnly_OvernightWindow_TreatsFridayNightAsWeekday()
    {
        var schedule = new ScheduleProfile
        {
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekdaysOnly,
        };
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 23)));  // Fri 23:00 → Friday
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 6, 2)));   // Sat 02:00 → Friday's window
        Assert.False(schedule.IsWithinMaintenanceWindow(At(2024, 1, 6, 23))); // Sat 23:00 → Saturday (weekend)
        Assert.False(schedule.IsWithinMaintenanceWindow(At(2024, 1, 7, 2)));  // Sun 02:00 → Saturday's window
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 8, 23)));  // Mon 23:00 → Monday
    }

    [Fact]
    public void WeekendsOnly_OvernightWindow()
    {
        var schedule = new ScheduleProfile
        {
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekendsOnly,
        };
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 6, 23)));  // Sat 23:00 → Saturday
        Assert.True(schedule.IsWithinMaintenanceWindow(At(2024, 1, 7, 2)));   // Sun 02:00 → Saturday's window
        Assert.False(schedule.IsWithinMaintenanceWindow(At(2024, 1, 5, 23))); // Fri 23:00 → weekday
    }

    // Local-wall-clock anchor: GetNextRun works in local time, so window tests pin the local date.
    private static DateTimeOffset LocalAt(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    [Fact]
    public void GetNextRun_Daily_InsideOvernightWindow_IsUnchanged()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Daily,
            TimeOfDay = new TimeOnly(23, 30),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
        };
        var unconstrained = new ScheduleProfile { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(23, 30) };
        var from = LocalAt(2024, 1, 5, 12);

        var next = schedule.GetNextRun(from);

        Assert.NotNull(next);
        Assert.Equal(unconstrained.GetNextRun(from), next); // 23:30 is in-window → no advance
        Assert.True(schedule.IsWithinMaintenanceWindow(next!.Value));
    }

    [Fact]
    public void GetNextRun_Daily_WeekdaysOnlyWindow_WalksPastTheWeekend()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Daily,
            TimeOfDay = new TimeOnly(23, 0),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekdaysOnly,
        };

        // Saturday local noon → Sat 23:00 and Sun 23:00 open weekend windows; Monday runs.
        var next = schedule.GetNextRun(LocalAt(2024, 1, 6, 12));

        Assert.NotNull(next);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(DayOfWeek.Monday, local.DayOfWeek);
        Assert.Equal(23, local.Hour);
        Assert.True(schedule.IsWithinMaintenanceWindow(next.Value));
    }

    [Fact]
    public void GetNextRun_Weekly_OvernightWindowAttribution_KeepsSundayEarlyMorning()
    {
        // Sunday 02:00 belongs to SATURDAY's overnight window, so a weekends-only scope allows it.
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

        var next = schedule.GetNextRun(LocalAt(2024, 1, 5, 12));

        Assert.NotNull(next);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(DayOfWeek.Sunday, local.DayOfWeek);
        Assert.Equal(2, local.Hour);
        Assert.True(schedule.IsWithinMaintenanceWindow(next.Value));
    }

    [Fact]
    public void GetNextRun_Hourly_AdvancesIntoWindow()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = 1,
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
        };

        var next = schedule.GetNextRun(At(2024, 1, 5, 12));

        Assert.NotNull(next);
        Assert.True(schedule.IsWithinMaintenanceWindow(next!.Value));
    }

    [Fact]
    public void Describe_IncludesTheWindow()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = 1,
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekdaysOnly,
        };

        var text = schedule.Describe();

        Assert.Contains("22:00", text);
        Assert.Contains("05:00", text);
        Assert.Contains("weekdays", text);
    }
}
