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

    // --- Maintenance window ---
    // Restricts SCHEDULED runs to an allowed time-of-day (and day) range so Obsync stays off the
    // server during business hours. Manual "Run Now" always bypasses the window.

    /// <summary>When true, scheduled runs only start inside the window below.</summary>
    public bool MaintenanceWindowEnabled { get; set; }

    /// <summary>Local time the window opens (e.g. 22:00). May be later than <see cref="WindowEnd"/> to wrap midnight.</summary>
    public TimeOnly WindowStart { get; set; } = new(22, 0);

    /// <summary>Local time the window closes (e.g. 05:00).</summary>
    public TimeOnly WindowEnd { get; set; } = new(5, 0);

    /// <summary>Which days the window applies to.</summary>
    public MaintenanceDayScope DayScope { get; set; } = MaintenanceDayScope.AnyDay;

    /// <summary>
    /// Whether <paramref name="localNow"/> is inside the maintenance window (always true when the window
    /// is disabled). The time range wraps midnight when <see cref="WindowStart"/> &gt; <see cref="WindowEnd"/>;
    /// the day check uses the day the window opened, so an overnight "weeknights" window treats
    /// Friday 22:00–Saturday 05:00 as a Friday window.
    /// </summary>
    public bool IsWithinMaintenanceWindow(DateTimeOffset localNow)
    {
        if (!MaintenanceWindowEnabled)
        {
            return true;
        }

        var time = TimeOnly.FromDateTime(localNow.DateTime);
        var inTimeRange = WindowStart <= WindowEnd
            ? time >= WindowStart && time < WindowEnd
            : time >= WindowStart || time < WindowEnd; // wraps midnight
        if (!inTimeRange)
        {
            return false;
        }

        var windowDay = WindowStart > WindowEnd && time < WindowEnd
            ? localNow.AddDays(-1).DayOfWeek
            : localNow.DayOfWeek;

        return DayScope switch
        {
            MaintenanceDayScope.WeekdaysOnly => windowDay is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            MaintenanceDayScope.WeekendsOnly => windowDay is DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true,
        };
    }

    /// <summary>
    /// Computes the next run time after <paramref name="fromUtc"/> for the standard cadences
    /// (hourly/daily/weekly), in local time. Returns null for manual schedules and for
    /// <see cref="ScheduleKind.Cron"/> (whose exact next fire is computed by the scheduler, which
    /// owns a cron engine). This is a dependency-free preview used by the app and the engine so the
    /// "Next run" column is populated without pulling a cron library into every layer.
    /// </summary>
    public DateTimeOffset? GetNextRun(DateTimeOffset fromUtc)
    {
        // Work in the local wall-clock domain and convert at the end: building candidates with
        // TODAY'S offset would mislabel a fire time that falls on the other side of a DST
        // transition (Local() applies the offset in effect at the candidate's own date).
        var now = fromUtc.ToLocalTime();
        switch (Kind)
        {
            case ScheduleKind.Hourly:
            {
                var step = IntervalHours <= 0 ? 1 : IntervalHours;
                var candidate = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
                while (candidate.Hour % step != 0)
                {
                    candidate = candidate.AddHours(1);
                }

                // A maintenance window can skip most hourly fires — advance to the next in-window hour so
                // the displayed "next run" is the time the job will actually run. Bounded to ~8 days.
                for (var i = 0; MaintenanceWindowEnabled && !IsWithinMaintenanceWindow(Local(candidate)) && i < 200; i++)
                {
                    candidate = candidate.AddHours(step);
                }

                return Local(candidate);
            }

            case ScheduleKind.Daily:
            {
                var candidate = At(now.Date, TimeOfDay);
                return Local(candidate <= now.DateTime ? candidate.AddDays(1) : candidate);
            }

            case ScheduleKind.Weekly:
            {
                var daysUntil = ((int)DayOfWeek - (int)now.DayOfWeek + 7) % 7;
                var candidate = At(now.Date.AddDays(daysUntil), TimeOfDay);
                return Local(candidate <= now.DateTime ? candidate.AddDays(7) : candidate);
            }

            default:
                return null; // Manual (no schedule) or Cron (scheduler computes the exact fire time).
        }
    }

    private static DateTime At(DateTime date, TimeOnly time) => date.Date + time.ToTimeSpan();

    /// <summary>A local wall-clock time as a DateTimeOffset, with the UTC offset in effect at THAT date.</summary>
    private static DateTimeOffset Local(DateTime wallClock) =>
        new(DateTime.SpecifyKind(wallClock, DateTimeKind.Local));

    /// <summary>A short, human-readable description such as "Daily at 23:00".</summary>
    public string Describe()
    {
        var cadence = Kind switch
        {
            ScheduleKind.Manual => "Manual only",
            ScheduleKind.Hourly => IntervalHours <= 1 ? "Every hour" : $"Every {IntervalHours} hours",
            ScheduleKind.Daily => $"Daily at {TimeOfDay:HH:mm}",
            ScheduleKind.Weekly => $"Weekly on {DayOfWeek} at {TimeOfDay:HH:mm}",
            ScheduleKind.Cron => $"Cron: {CronExpression}",
            _ => "Unknown",
        };

        if (!MaintenanceWindowEnabled)
        {
            return cadence;
        }

        var days = DayScope switch
        {
            MaintenanceDayScope.WeekdaysOnly => ", weekdays",
            MaintenanceDayScope.WeekendsOnly => ", weekends",
            _ => string.Empty,
        };
        return $"{cadence} · within {WindowStart:HH:mm}–{WindowEnd:HH:mm}{days}";
    }
}
