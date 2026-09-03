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

    /// <summary>
    /// Next fire time (UTC) of a raw cron expression after <paramref name="afterUtc"/>, or null when it
    /// never fires again (e.g. a past year, Feb 31). Quartz throws for a trigger that never fires, so
    /// callers must check this before handing the expression to the scheduler.
    /// </summary>
    public static DateTimeOffset? NextFire(string cron, DateTimeOffset afterUtc) =>
        new CronExpression(cron) { TimeZone = TimeZoneInfo.Local }.GetNextValidTimeAfter(afterUtc);

    /// <summary>
    /// True when a live trigger is already running the cadence <paramref name="cron"/> asks for.
    /// <paramref name="triggerExpression"/> is the expression read back from the trigger, and null
    /// when the job has no trigger at all.
    /// </summary>
    /// <remarks>
    /// Quartz keeps an expression in its own normal form: building a trigger upper-cases the day and
    /// month names. So the text the app saved differs from the text the trigger reports whenever the
    /// user typed a name in anything but upper case — <c>0 0 2 ? * mon-fri</c> is stored verbatim but
    /// comes back as <c>0 0 2 ? * MON-FRI</c> — even though the two mean exactly the same thing.
    /// Comparing the raw strings therefore makes reconcile believe the cadence changed on every tick
    /// and tear the trigger down and rebuild it forever, so the comparison is made in that normal form
    /// on both sides. Deriving it from Quartz rather than upper-casing here keeps the two in step if
    /// Quartz ever normalizes something else as well.
    /// <para>
    /// <paramref name="cron"/> must already be valid — every caller gates on
    /// <see cref="CronExpression.IsValidExpression"/> first — because parsing it is how the normal
    /// form is obtained, exactly as in <see cref="NextFire"/>.
    /// </para>
    /// </remarks>
    public static bool MatchesTrigger(string? triggerExpression, string cron) =>
        triggerExpression is not null &&
        string.Equals(triggerExpression, new CronExpression(cron).CronExpressionString, StringComparison.Ordinal);

    // Quartz day-of-week is 1=Sunday .. 7=Saturday; .NET DayOfWeek is 0=Sunday.
    private static int QuartzDayOfWeek(DayOfWeek day) => (int)day + 1;
}
