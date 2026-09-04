using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// The next run time for any cadence, including the one <see cref="ScheduleProfile.GetNextRun"/>
/// cannot answer.
///
/// That method returns null for <see cref="ScheduleKind.Cron"/> because Obsync.Shared deliberately
/// has no cron engine. Null therefore means several different things to a caller — manual, never
/// runs, or "cron, ask the scheduler" — and a caller that reads it as "no next run" shows a job with
/// no next run at all. The app has Quartz, so here it does not have to guess. Same placement and
/// shape as <see cref="ScheduleWindowGuard"/>, and used by every app-side caller so the wizard and
/// the Jobs list cannot answer this differently.
///
/// Deliberately reports the next fire itself, not the next fire a maintenance window would admit:
/// that matches what the service's reconcile writes, so the app and the service agree rather than
/// alternating. The window-versus-raw disagreement is its own open issue.
/// </summary>
public static class ScheduleNextRun
{
    /// <summary>
    /// The next occurrence after <paramref name="fromUtc"/>, or null when the schedule genuinely has
    /// none — manual, or a cadence its maintenance window can never admit. <paramref name="timeZone"/>
    /// defaults to the machine's, which is the zone the scheduler builds its triggers in.
    /// </summary>
    public static DateTimeOffset? Compute(
        ScheduleProfile schedule, DateTimeOffset fromUtc, TimeZoneInfo? timeZone = null)
    {
        if (schedule.Kind != ScheduleKind.Cron)
        {
            return schedule.GetNextRun(fromUtc);
        }

        // A window that admits nothing starves a cron expression just as thoroughly, and that much
        // this layer can answer without a cron engine.
        var cron = schedule.CronExpression?.Trim();
        return !string.IsNullOrWhiteSpace(cron)
            && !schedule.NeverRunsInsideItsWindow()
            && Quartz.CronExpression.IsValidExpression(cron)
                ? new Quartz.CronExpression(cron) { TimeZone = timeZone ?? TimeZoneInfo.Local }
                    .GetNextValidTimeAfter(fromUtc)
                : null;
    }
}
