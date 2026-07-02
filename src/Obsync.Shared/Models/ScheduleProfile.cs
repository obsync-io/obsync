namespace Obsync.Shared.Models;

/// <summary>
/// When a job runs. Pure configuration data — translation to a Quartz trigger lives in
/// <c>Obsync.Scheduler</c>.
/// </summary>
public sealed class ScheduleProfile
{
    public ScheduleKind Kind { get; set; } = ScheduleKind.Manual;

    /// <summary>Interval in hours for <see cref="ScheduleKind.Hourly"/> (1 = every hour).</summary>
    public int IntervalHours { get; set; } = 1;

    /// <summary>Local time of day for <see cref="ScheduleKind.Daily"/> / <see cref="ScheduleKind.Weekly"/>.</summary>
    public TimeOnly TimeOfDay { get; set; } = new(23, 0);

    /// <summary>Day of week for <see cref="ScheduleKind.Weekly"/>.</summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>Custom cron expression for <see cref="ScheduleKind.Cron"/> (Quartz 7-field format).</summary>
    public string? CronExpression { get; set; }

    /// <summary>Also run once when the service / app starts.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Skip committing when no object changes are detected (a run still records history).</summary>
    public bool RunOnlyIfChanges { get; set; } = true;

    /// <summary>
    /// Computes the next run time after <paramref name="fromUtc"/> for the standard cadences
    /// (hourly/daily/weekly), in local time. Returns null for manual schedules and for
    /// <see cref="ScheduleKind.Cron"/> (whose exact next fire is computed by the scheduler, which
    /// owns a cron engine). This is a dependency-free preview used by the app and the engine so the
    /// "Next run" column is populated without pulling a cron library into every layer.
    /// </summary>
    public DateTimeOffset? GetNextRun(DateTimeOffset fromUtc)
    {
        var now = fromUtc.ToLocalTime();
        switch (Kind)
        {
            case ScheduleKind.Hourly:
            {
                var step = IntervalHours <= 0 ? 1 : IntervalHours;
                var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);
                while (candidate.Hour % step != 0)
                {
                    candidate = candidate.AddHours(1);
                }

                return candidate;
            }

            case ScheduleKind.Daily:
            {
                var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, TimeOfDay.Hour, TimeOfDay.Minute, 0, now.Offset);
                return candidate <= now ? candidate.AddDays(1) : candidate;
            }

            case ScheduleKind.Weekly:
            {
                var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, TimeOfDay.Hour, TimeOfDay.Minute, 0, now.Offset);
                var daysUntil = ((int)DayOfWeek - (int)now.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(daysUntil);
                return candidate <= now ? candidate.AddDays(7) : candidate;
            }

            default:
                return null; // Manual (no schedule) or Cron (scheduler computes the exact fire time).
        }
    }

    /// <summary>A short, human-readable description such as "Daily at 23:00".</summary>
    public string Describe() => Kind switch
    {
        ScheduleKind.Manual => "Manual only",
        ScheduleKind.Hourly => IntervalHours <= 1 ? "Every hour" : $"Every {IntervalHours} hours",
        ScheduleKind.Daily => $"Daily at {TimeOfDay:HH:mm}",
        ScheduleKind.Weekly => $"Weekly on {DayOfWeek} at {TimeOfDay:HH:mm}",
        ScheduleKind.Cron => $"Cron: {CronExpression}",
        _ => "Unknown",
    };
}
