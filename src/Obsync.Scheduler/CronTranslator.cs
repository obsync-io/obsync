using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.Scheduler;

/// <summary>Translates a <see cref="ScheduleProfile"/> into a Quartz cron expression and next-run time.</summary>
public static class CronTranslator
{
    /// <summary>Returns the Quartz cron expression for a schedule, or null when it is manual-only.</summary>
    public static string? ToCron(ScheduleProfile schedule)
    {
        var time = schedule.TimeOfDay;
        return schedule.Kind switch
        {
            ScheduleKind.Manual => null,
            ScheduleKind.Hourly => schedule.IntervalHours <= 1 ? "0 0 * * * ?" : $"0 0 0/{schedule.IntervalHours} * * ?",
            ScheduleKind.Daily => $"0 {time.Minute} {time.Hour} * * ?",
            ScheduleKind.Weekly => $"0 {time.Minute} {time.Hour} ? * {QuartzDayOfWeek(schedule.DayOfWeek)}",
            ScheduleKind.Cron => schedule.CronExpression,
            _ => null,
        };
    }

    /// <summary>Computes the next run time (UTC) after <paramref name="afterUtc"/>, or null if not scheduled/invalid.</summary>
    public static DateTimeOffset? NextRun(ScheduleProfile schedule, DateTimeOffset afterUtc)
    {
        var cron = ToCron(schedule);
        if (string.IsNullOrWhiteSpace(cron) || !CronExpression.IsValidExpression(cron))
        {
            return null;
        }

        return new CronExpression(cron) { TimeZone = TimeZoneInfo.Local }.GetNextValidTimeAfter(afterUtc);
    }

    /// <summary>True when the schedule is a valid, non-manual trigger.</summary>
    public static bool IsValid(ScheduleProfile schedule)
    {
        var cron = ToCron(schedule);
        return !string.IsNullOrWhiteSpace(cron) && CronExpression.IsValidExpression(cron);
    }

    // Quartz day-of-week is 1=Sunday .. 7=Saturday; .NET DayOfWeek is 0=Sunday.
    private static int QuartzDayOfWeek(DayOfWeek day) => (int)day + 1;
}
