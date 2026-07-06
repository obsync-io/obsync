using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine.Alerting;

/// <summary>Decides whether a finished run should produce an alert, per the global alert settings.</summary>
public static class RunAlertEvaluator
{
    /// <summary>
    /// True when <paramref name="run"/> matches an enabled trigger on an enabled channel.
    /// Cancelled and no-change runs never alert; a succeeded run alerts only when the
    /// changes trigger is on and the run actually changed something.
    /// </summary>
    public static bool ShouldAlert(AlertSettings settings, SyncRun run)
    {
        if (!settings.EmailEnabled && !settings.WebhookEnabled)
        {
            return false;
        }

        if (settings.ScheduledRunsOnly && run.Trigger == RunTrigger.Manual)
        {
            return false;
        }

        return run.Status switch
        {
            RunStatus.Failed => settings.OnFailure,
            RunStatus.Warning => settings.OnWarning,
            RunStatus.Succeeded => settings.OnChanges && run.ChangeCount > 0,
            _ => false,
        };
    }
}
