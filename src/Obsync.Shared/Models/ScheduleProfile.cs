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

    /// <summary>
    /// Largest schedulable hourly interval. The Quartz translation puts the interval in a step of
    /// the cron HOUR field (<c>0 0 0/N * * ?</c>), and a step there accepts only 0-23 — a larger
    /// value yields an expression Quartz rejects, which would leave the job enabled but never
    /// triggered.
    /// </summary>
    public const int MaxHourlyInterval = 23;

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
        // A window that can never admit this schedule has no next run to report, and saying so is
        // the whole difference between "here is when it runs" and "I stopped looking". Answering it
        // exactly, up front, is also what makes the bounded loops below unable to give up: they now
        // only ever run for a schedule that HAS an occurrence to find, so their bounds are backstops
        // rather than verdicts.
        if (NeverRunsInsideItsWindow())
        {
            return null;
        }

        // Work in the local wall-clock domain and convert at the end: building candidates with
        // TODAY'S offset would mislabel a fire time that falls on the other side of a DST
        // transition (Local() applies the offset in effect at the candidate's own date).
        var now = fromUtc.ToLocalTime();
        switch (Kind)
        {
            case ScheduleKind.Hourly:
            {
                var step = IntervalHours <= 0 ? 1 : IntervalHours;
                var candidate = NextHourlyFireAfter(
                    new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0), step);

                // A maintenance window can skip most hourly fires — advance to the next in-window fire so
                // the displayed "next run" is a time the job will actually run. It has to advance along the
                // grid, NOT by `step`: the trigger is `0 0 0/N * * ?`, whose step restarts at hour 0 every
                // midnight, so a uniform N-hour stride leaves the grid the moment it crosses midnight for
                // any N that does not divide 24 — and the loop then stops on whatever off-grid hour the
                // window happens to admit, reporting a time the job can never fire at.
                // Bounded at 200 fires: over a week even on the densest grid (hourly), and a day-scoped
                // window can defer a run by at most a week.
                for (var i = 0; MaintenanceWindowEnabled && !IsWithinMaintenanceWindow(Local(candidate)) && i < 200; i++)
                {
                    candidate = NextHourlyFireAfter(candidate, step);
                }

                return AdmittedOrNull(candidate);
            }

            case ScheduleKind.Daily:
            {
                var candidate = At(now.Date, TimeOfDay);
                if (candidate <= now.DateTime)
                {
                    candidate = candidate.AddDays(1);
                }

                // Same window-advance as Hourly: a day-scoped window (e.g. weekdays-only) skips some
                // daily fires, so walk to the first day that will actually run. Bounded to ~2 months.
                for (var i = 0; MaintenanceWindowEnabled && !IsWithinMaintenanceWindow(Local(candidate)) && i < 60; i++)
                {
                    candidate = candidate.AddDays(1);
                }

                return AdmittedOrNull(candidate);
            }

            case ScheduleKind.Weekly:
            {
                var daysUntil = ((int)DayOfWeek - (int)now.DayOfWeek + 7) % 7;
                var candidate = At(now.Date.AddDays(daysUntil), TimeOfDay);
                if (candidate <= now.DateTime)
                {
                    candidate = candidate.AddDays(7);
                }

                for (var i = 0; MaintenanceWindowEnabled && !IsWithinMaintenanceWindow(Local(candidate)) && i < 60; i++)
                {
                    candidate = candidate.AddDays(7);
                }

                return AdmittedOrNull(candidate);
            }

            default:
                return null; // Manual (no schedule) or Cron (scheduler computes the exact fire time).
        }
    }

    private static DateTime At(DateTime date, TimeOnly time) => date.Date + time.ToTimeSpan();

    /// <summary>
    /// The candidate as a local instant, or null when the maintenance window still does not admit it.
    /// Only a loop that gave up can land here, which the pre-check in <see cref="GetNextRun"/> makes
    /// unreachable — but handing back the out-of-window candidate anyway is exactly what made "here is
    /// the next run" indistinguishable from "I stopped looking", so it is not done.
    /// </summary>
    private DateTimeOffset? AdmittedOrNull(DateTime candidate) =>
        MaintenanceWindowEnabled && !IsWithinMaintenanceWindow(Local(candidate)) ? null : Local(candidate);

    /// <summary>
    /// The first Hourly occurrence strictly after <paramref name="hour"/> (a whole hour), on the grid
    /// the scheduler's cron actually uses: minute zero, on hours where <c>hour % step == 0</c>, with
    /// the step restarting at midnight. Note that grid is NOT "every step hours" — for a step that
    /// does not divide 24 the last gap of the day is short (step 5 runs 20:00 then 00:00, four hours
    /// later), which is exactly why advancing by the step drifts off it. Same rule as
    /// <see cref="HourlyFireTimes"/>; used for both the first candidate and the window advance so the
    /// two cannot disagree.
    /// </summary>
    private static DateTime NextHourlyFireAfter(DateTime hour, int step)
    {
        var next = hour.AddHours(1);
        while (next.Hour % step != 0)
        {
            next = next.AddHours(1);
        }

        return next;
    }

    /// <summary>A local wall-clock time as a DateTimeOffset, with the UTC offset in effect at THAT date.</summary>
    private static DateTimeOffset Local(DateTime wallClock) =>
        new(ExistingLocalTime(wallClock, TimeZoneInfo.Local));

    /// <summary>
    /// <paramref name="wallClock"/> if the zone has such a time, else the first minute after the gap
    /// that it does.
    ///
    /// On the spring-forward date an hour of wall-clock time does not occur. Constructing a
    /// DateTimeOffset from one anyway yields an instant on the far side of the gap — so a 02:30 daily
    /// job resolved to 03:30, which a maintenance window admitting 02:00–03:00 then rejected, and the
    /// preview reported a time inside an hour that never happened. Moving to the first time that does
    /// exist matches what the clock does, and makes the window see the instant the run really takes.
    ///
    /// A minute at a time rather than by a computed offset: gaps are not all an hour (Lord Howe
    /// shifts thirty minutes), and this is obviously correct at the cost of a loop that only ever
    /// spins on one date a year. Takes the zone as a parameter so the behaviour is testable off a
    /// machine that observes DST at all.
    /// </summary>
    internal static DateTime ExistingLocalTime(DateTime wallClock, TimeZoneInfo zone)
    {
        // Probed as Unspecified on purpose: IsInvalidTime answers false for a Local-kind value unless
        // the zone IS the system-local one, so asking with Local kind silently never detects a gap.
        var probe = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        for (var i = 0; zone.IsInvalidTime(probe) && i < 180; i++)
        {
            probe = probe.AddMinutes(1);
        }

        return DateTime.SpecifyKind(probe, DateTimeKind.Local);
    }

    /// <summary>
    /// Why this schedule cannot be turned into a trigger, or null when it can. Every entry point
    /// that persists a schedule checks this, so the app, the job-config importer, and the service
    /// can never disagree about what is schedulable — the disagreement is what let an unschedulable
    /// job be saved with a confident "next run" it would never honour.
    /// </summary>
    public string? UnschedulableReason() =>
        Kind == ScheduleKind.Hourly && IntervalHours > MaxHourlyInterval
            ? $"An hourly interval must be {MaxHourlyInterval} hours or less. For a once-a-day sync choose the "
                + "Daily cadence; for a longer gap use a Cron expression (for example '0 0 3 1/2 * ?' runs at "
                + "03:00 every second day)."
            : null;

    /// <summary>
    /// Whether an enabled maintenance window can never admit this schedule, so it has no next run at
    /// all. The verdict behind <see cref="MaintenanceWindowConflictReason"/>, named for the callers
    /// that want the answer rather than the explanation — <see cref="GetNextRun"/> and the Jobs list.
    /// Kept as a wrapper so there is exactly one definition of what "never runs" means.
    /// </summary>
    public bool NeverRunsInsideItsWindow() => MaintenanceWindowConflictReason() is not null;

    /// <summary>
    /// Why an enabled maintenance window can never admit this schedule, or null when at least one
    /// occurrence lands inside it. Companion to <see cref="UnschedulableReason"/>: that one asks
    /// whether a trigger can be built, this one asks whether the trigger's fires can ever get past
    /// the window.
    ///
    /// A starved combination is the quietest failure this product has — the trigger is healthy,
    /// "Next run" advances, and the engine skips every occurrence with a single Information log
    /// line and no history row — so it is refused before it can be saved.
    ///
    /// Covers the cadences whose fire times this profile fully determines. <see cref="ScheduleKind.Cron"/>
    /// returns null here: its fire times need a cron engine, which this layer deliberately does not
    /// reference (see <see cref="GetNextRun"/>), so a caller that has one checks it separately.
    /// </summary>
    public string? MaintenanceWindowConflictReason()
    {
        // Manual has nothing to starve: the window gates Scheduled and CatchUp runs only, and
        // "Run Now" and startup runs bypass it.
        if (!MaintenanceWindowEnabled || Kind == ScheduleKind.Manual)
        {
            return null;
        }

        // Equal bounds take the non-wrapping branch of IsWithinMaintenanceWindow, which asks for
        // `time >= start && time < end` — false for all 1440 minutes of the day. That starves every
        // cadence, so it is checked before, and independently of, the fire times.
        if (WindowStart == WindowEnd)
        {
            return $"The maintenance window opens and closes at the same time ({WindowStart:HH:mm}), so no run "
                + "would ever fall inside it and the job would never run. Give the window an end time that "
                + "differs from its start.";
        }

        var days = DayScope switch
        {
            MaintenanceDayScope.WeekdaysOnly => ", weekdays",
            MaintenanceDayScope.WeekendsOnly => ", weekends",
            _ => string.Empty,
        };
        var window = $"({WindowStart:HH:mm}–{WindowEnd:HH:mm}{days})";

        switch (Kind)
        {
            case ScheduleKind.Hourly:
            {
                var hours = HourlyFireTimes();
                if (hours.Any(hour => AllDays.Any(day => Admits(day, hour))))
                {
                    return null;
                }

                // Naming the fire times matters here in a way it does not for Daily/Weekly: the
                // usual cause is a window that opens or closes mid-hour (02:15–02:45 admits no
                // HH:00 at all), and the user has never been shown that hourly runs land on :00.
                var fires = hours.Count >= 24
                    ? "on the hour, every hour"
                    : $"at {string.Join(", ", hours.Select(hour => hour.ToString("HH:mm")))}";
                return $"An hourly schedule runs {fires}, and none of those times ever falls inside the "
                    + $"maintenance window {window}, so the job would never run. Widen the window, or change "
                    + "the interval.";
            }

            case ScheduleKind.Daily:
                return AllDays.Any(day => Admits(day, TimeOfDay))
                    ? null
                    : $"Daily at {TimeOfDay:HH:mm} never falls inside the maintenance window " +
                      $"({WindowStart:HH:mm}–{WindowEnd:HH:mm}), so the job would never run. Change the time or the window.";

            case ScheduleKind.Weekly:
                return Admits(DayOfWeek, TimeOfDay)
                    ? null
                    : $"Weekly on {DayOfWeek} at {TimeOfDay:HH:mm} never falls inside the maintenance window " +
                      $"{window}, so the job would never run. Change the schedule or the window.";

            default:
                return null; // Cron — see the remark on this method.
        }
    }

    /// <summary>2024-01-01 is a Monday, so one anchored week covers every day-of-week case exactly once.</summary>
    private static readonly DateTime ProbeWeekMonday = new(2024, 1, 1);

    private static readonly DayOfWeek[] AllDays = Enum.GetValues<DayOfWeek>();

    /// <summary>
    /// Whether the window would admit an occurrence on <paramref name="day"/> at <paramref name="time"/>.
    /// Asked through <see cref="IsWithinMaintenanceWindow"/> itself rather than re-deriving the rule, so
    /// an overnight window attributes the occurrence to the day the window opened exactly as the engine
    /// does at run time.
    /// </summary>
    private bool Admits(DayOfWeek day, TimeOnly time) =>
        // Sunday is 0 in DayOfWeek; shift by 6 so Monday — the anchor — lands on index 0.
        IsWithinMaintenanceWindow(new DateTimeOffset(
            ProbeWeekMonday.AddDays(((int)day + 6) % 7) + time.ToTimeSpan(), TimeSpan.Zero));

    /// <summary>
    /// The local times an Hourly schedule fires at, mirroring the cron the scheduler builds
    /// (<c>0 0 * * * ?</c>, or <c>0 0 0/N * * ?</c> for an interval above 1): always minute zero, on
    /// hours that are multiples of the interval, and the step restarts at midnight.
    /// </summary>
    private List<TimeOnly> HourlyFireTimes()
    {
        var step = IntervalHours <= 0 ? 1 : IntervalHours;
        var times = new List<TimeOnly>();
        for (var hour = 0; hour < 24; hour++)
        {
            if (hour % step == 0)
            {
                times.Add(new TimeOnly(hour, 0));
            }
        }

        return times;
    }

    /// <summary>
    /// How an Hourly cadence reads in one line. "Every N hours" is only true when N divides the day
    /// evenly: the trigger is <c>0 0 0/N * * ?</c>, whose step restarts at midnight, so for any other
    /// N the interval is not a period at all — "every 23 hours" runs twice a day, 23 hours apart and
    /// then one hour apart. Those are the cases that name their times instead, which stays short
    /// because an N that does not divide 24 has at most five occurrences a day (N=5 is the worst).
    /// </summary>
    private string DescribeHourlyCadence()
    {
        var step = IntervalHours <= 0 ? 1 : IntervalHours;
        if (step == 1)
        {
            return "Every hour";
        }

        if (step <= MaxHourlyInterval && 24 % step == 0)
        {
            return $"Every {step} hours";
        }

        var times = string.Join(", ", HourlyFireTimes().Select(hour => hour.ToString("HH:mm")));
        return $"Every {step} hours from midnight ({times})";
    }

    /// <summary>
    /// The fuller account of an Hourly cadence for the place the interval is chosen: when it runs,
    /// and — when the interval does not divide 24 — the short last gap of the day that makes
    /// "every N hours" untrue. Null for every other cadence. Separate from <see cref="Describe"/>
    /// because that one has to stay to a single line in a table cell, while this one is guidance.
    /// </summary>
    public string? DescribeHourlyPattern()
    {
        if (Kind != ScheduleKind.Hourly)
        {
            return null;
        }

        var step = IntervalHours <= 0 ? 1 : IntervalHours;
        var fires = HourlyFireTimes();

        // Listing 24 or 12 times would be noise; those intervals divide the day evenly anyway, so
        // naming the cadence says everything the times would.
        var runs = fires.Count <= 6
            ? $"Runs at {string.Join(", ", fires.Select(hour => hour.ToString("HH:mm")))}."
            : step == 1
                ? "Runs on the hour, every hour."
                : $"Runs on the hour, every {step} hours.";

        var remainder = 24 % step;
        if (step > MaxHourlyInterval || remainder == 0)
        {
            return runs;
        }

        var gap = remainder == 1 ? "1 hour" : $"{remainder} hours";
        return $"{runs} The schedule restarts at midnight, so the last gap of the day is {gap}, not {step}.";
    }

    /// <summary>A short, human-readable description such as "Daily at 23:00".</summary>
    public string Describe()
    {
        var cadence = Kind switch
        {
            ScheduleKind.Manual => "Manual only",
            ScheduleKind.Hourly => DescribeHourlyCadence(),
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
