using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.App.Services;

/// <summary>
/// The maintenance-window compatibility rule that every entry point persisting a schedule applies.
///
/// Most of it is <see cref="ScheduleProfile.MaintenanceWindowConflictReason"/>. This adds the one
/// case that layer cannot answer — a Cron expression, whose fire times need a cron engine that
/// Obsync.Shared deliberately does not reference. It lives here because Quartz is already a
/// dependency of the app, and because both callers are: the wizard and the job-config importer.
/// Having the rule in one place is the point — the wizard and the importer disagreeing about what
/// is runnable is exactly what lets a job be saved with a confident next run it never honours.
/// </summary>
public static class ScheduleWindowGuard
{
    /// <summary>
    /// How far ahead a Cron schedule is probed for an occurrence the window would admit. A yearly
    /// expression fires once in ~366 days, so a shorter horizon would reject a good schedule purely
    /// for not having looked far enough.
    /// </summary>
    private static readonly TimeSpan CronProbeHorizon = TimeSpan.FromDays(400);

    /// <summary>
    /// Caps the probe so a dense expression cannot stall a save. Comfortably clears an hourly
    /// expression across the whole horizon (~9,600 fires). Reaching this cap means the horizon was
    /// NOT covered, which is why it produces "accept" rather than "reject" — see
    /// <see cref="ConflictReason"/>.
    /// </summary>
    private const int MaxCronProbeFires = 20_000;

    /// <summary>
    /// Why an enabled maintenance window can never admit <paramref name="schedule"/>, or null when
    /// it can. <paramref name="fromUtc"/> anchors the Cron probe. <paramref name="timeZone"/> defaults
    /// to the machine's, which is the one the scheduler and the engine both use; it is a parameter so
    /// the daylight-saving behaviour below is testable on a machine in any zone.
    /// </summary>
    public static string? ConflictReason(
        ScheduleProfile schedule, DateTimeOffset fromUtc, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;

        if (schedule.MaintenanceWindowConflictReason() is { } reason)
        {
            return reason;
        }

        if (!schedule.MaintenanceWindowEnabled || schedule.Kind != ScheduleKind.Cron)
        {
            return null;
        }

        // Syntax and never-fires are separate rules with their own messages. Say nothing about an
        // expression that has no fire times to test in the first place.
        var cron = schedule.CronExpression?.Trim();
        if (string.IsNullOrWhiteSpace(cron) || !CronExpression.IsValidExpression(cron))
        {
            return null;
        }

        var expression = new CronExpression(cron) { TimeZone = timeZone };
        var deadline = fromUtc + CronProbeHorizon;
        var at = fromUtc;
        var horizonCovered = false;
        for (var probe = 0; probe < MaxCronProbeFires; probe++)
        {
            if (expression.GetNextValidTimeAfter(at) is not { } fire)
            {
                horizonCovered = true; // The expression stops firing, so every fire it has was tested.
                break;
            }

            if (fire > deadline)
            {
                horizonCovered = true;
                break;
            }

            // IsSatisfiedBy filters out a fire the expression does not actually match. Quartz emits
            // one on the spring-forward day: the named local time does not exist, so the fire is
            // nudged to one that does — '0 30 2 * * ?' fires at 03:30 exactly once a year, which a
            // 03:00-05:00 window would otherwise accept as proof the schedule is fine. It is not the
            // schedule's cadence, so it does not count as the window admitting it. (Measured against
            // Quartz 3.18.2: of 400 fires it reports for that expression, that one is the only one
            // it does not satisfy.) The window test itself is the same conversion the engine's gate
            // makes, so the verdict here is the one it reaches.
            if (expression.IsSatisfiedBy(fire)
                && schedule.IsWithinMaintenanceWindow(TimeZoneInfo.ConvertTime(fire, timeZone)))
            {
                return null;
            }

            at = fire;
        }

        // Reject only on a COMPLETED scan. Running out of probes leaves the rest of the horizon
        // unexamined, and refusing a schedule that may well run is worse than missing one that never will.
        if (!horizonCovered)
        {
            return null;
        }

        var days = schedule.DayScope switch
        {
            MaintenanceDayScope.WeekdaysOnly => ", weekdays",
            MaintenanceDayScope.WeekendsOnly => ", weekends",
            _ => string.Empty,
        };
        // "fires only outside", not "never fires inside": on a spring-forward day the shifted fire
        // above can land in the window, so "never" would be a claim the code cannot stand behind.
        return $"The cron expression '{cron}' fires only outside the maintenance window "
            + $"({schedule.WindowStart:HH:mm}–{schedule.WindowEnd:HH:mm}{days}), so the job would never run "
            + "on its schedule. Change the expression or the window.";
    }
}
