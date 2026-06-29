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
