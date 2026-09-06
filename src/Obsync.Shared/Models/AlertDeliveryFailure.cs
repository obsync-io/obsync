namespace Obsync.Shared.Models;

/// <summary>
/// The last time an alert could not be delivered, kept so a silent alerting outage becomes visible
/// somewhere. Alert sends are deliberately best-effort — a failed notification must never fail or
/// delay the run that triggered it — but "logged and swallowed" left the only record in a file
/// nobody reads, while Settings still reported a green "Test alert sent".
/// </summary>
/// <remarks>
/// The gap this closes is an identity one, not a transport one. The test button sends from the APP,
/// under the signed-in user, whose Windows Credential Manager vault holds the SMTP password.
/// Scheduled runs send from the SERVICE, under its own account, whose vault does not — so
/// <c>Retrieve</c> returns null, the password degrades to empty, the relay answers 535, and every
/// alert is lost with the test still green. <see cref="Account"/> is recorded for exactly that
/// comparison: it names the account that failed, which is usually the whole diagnosis.
/// <para>
/// A mutable class with a parameterless constructor, not a positional record: this is persisted as
/// JSON through <c>ObsyncJson</c>, whose deserializer carries a <c>new()</c> constraint — the same
/// shape <c>SchedulerHeartbeat</c> uses for the same reason.
/// </para>
/// </remarks>
public sealed class AlertDeliveryFailure
{
    public DateTimeOffset AtUtc { get; set; }

    /// <summary>"Email" or "Webhook".</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>The run whose alert was lost, for correlation with History.</summary>
    public string RunKey { get; set; } = string.Empty;

    /// <summary>That run's job, so the message reads without a lookup.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>The Windows account the sending host was running as, e.g. <c>DOMAIN\user</c>.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>The sender's error, already redacted by the caller.</summary>
    public string Error { get; set; } = string.Empty;
}
