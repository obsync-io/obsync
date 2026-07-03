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
