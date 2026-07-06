using Obsync.Engine.Alerting;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine.Tests;

public sealed class RunAlertEvaluatorTests
{
    private static AlertSettings Settings(
        bool email = true, bool webhook = false,
        bool onFailure = true, bool onWarning = true, bool onChanges = false,
        bool scheduledOnly = true) => new()
    {
        EmailEnabled = email,
        WebhookEnabled = webhook,
        OnFailure = onFailure,
        OnWarning = onWarning,
        OnChanges = onChanges,
        ScheduledRunsOnly = scheduledOnly,
    };

    private static SyncRun Run(RunStatus status, RunTrigger trigger = RunTrigger.Scheduled, int added = 0) => new()
    {
        Status = status,
        Trigger = trigger,
        ObjectsAdded = added,
    };

    [Theory]
    // Failed follows the failure toggle.
    [InlineData(RunStatus.Failed, true, true, false, true)]
    [InlineData(RunStatus.Failed, false, true, true, false)]
    // Warning follows the warning toggle.
    [InlineData(RunStatus.Warning, true, true, false, true)]
    [InlineData(RunStatus.Warning, true, false, true, false)]
    // Succeeded-with-changes follows the changes toggle (the run below carries one change).
    [InlineData(RunStatus.Succeeded, true, true, true, true)]
    [InlineData(RunStatus.Succeeded, true, true, false, false)]
    public void ShouldAlert_FollowsTheTriggerToggles(
        RunStatus status, bool onFailure, bool onWarning, bool onChanges, bool expected)
    {
        var settings = Settings(onFailure: onFailure, onWarning: onWarning, onChanges: onChanges);
        var run = Run(status, added: 1);

        Assert.Equal(expected, RunAlertEvaluator.ShouldAlert(settings, run));
    }

    [Theory]
    [InlineData(RunStatus.Pending)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.NoChanges)]
    [InlineData(RunStatus.Cancelled)]
    public void ShouldAlert_NeverForNonTerminalOrQuietStatuses(RunStatus status)
    {
        var settings = Settings(onChanges: true);

        Assert.False(RunAlertEvaluator.ShouldAlert(settings, Run(status, added: 5)));
    }

    [Fact]
    public void ShouldAlert_SucceededWithoutChanges_DoesNotAlert_EvenWithChangesOn()
    {
        var settings = Settings(onChanges: true);

        Assert.False(RunAlertEvaluator.ShouldAlert(settings, Run(RunStatus.Succeeded, added: 0)));
    }

    [Theory]
    [InlineData(RunTrigger.Manual, true, false)]
    [InlineData(RunTrigger.Scheduled, true, true)]
    [InlineData(RunTrigger.Startup, true, true)]
    [InlineData(RunTrigger.Manual, false, true)]
    public void ShouldAlert_ScheduledRunsOnly_FiltersManualRuns(RunTrigger trigger, bool scheduledOnly, bool expected)
    {
        var settings = Settings(scheduledOnly: scheduledOnly);

        Assert.Equal(expected, RunAlertEvaluator.ShouldAlert(settings, Run(RunStatus.Failed, trigger)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldAlert_RequiresAtLeastOneEnabledChannel(bool email, bool webhook, bool expected)
    {
        var settings = Settings(email: email, webhook: webhook);

        Assert.Equal(expected, RunAlertEvaluator.ShouldAlert(settings, Run(RunStatus.Failed)));
    }
}
