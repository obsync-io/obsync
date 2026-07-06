namespace Obsync.Shared.Models;

/// <summary>
/// Global run-alert configuration (SMTP email + generic webhook), shared by the app and the
/// Windows service. The SMTP password is never stored here — it lives in Windows Credential
/// Manager, keyed by <see cref="Obsync.Shared.Abstractions.CredentialKeys.SmtpPassword"/>.
/// </summary>
public sealed class AlertSettings
{
    /// <summary>Send alerts by SMTP email.</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>The SMTP server host name, e.g. <c>smtp.corp.example</c>.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>The SMTP server port. Defaults to 587 (submission with STARTTLS).</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Negotiate TLS for the SMTP connection.</summary>
    public bool SmtpUseTls { get; set; } = true;

    /// <summary>Optional SMTP username for an authenticated relay.</summary>
    public string? SmtpUsername { get; set; }

    /// <summary>The alert sender address.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Comma-separated recipient addresses.</summary>
    public string? ToAddresses { get; set; }

    /// <summary>Send alerts as a JSON POST to a webhook URL (Teams, Slack, anything).</summary>
    public bool WebhookEnabled { get; set; }

    /// <summary>The webhook endpoint; must be an absolute http(s) URL.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Alert when a run fails.</summary>
    public bool OnFailure { get; set; } = true;

    /// <summary>Alert when a run finishes with warnings.</summary>
    public bool OnWarning { get; set; } = true;

    /// <summary>Alert when a run detects and commits changes. Off by default — noisy.</summary>
    public bool OnChanges { get; set; }

    /// <summary>Only alert for scheduled/startup runs — manual runs have the user watching.</summary>
    public bool ScheduledRunsOnly { get; set; } = true;

    /// <summary>True when an SMTP password should be present (authenticated relay with email on).</summary>
    public bool RequiresPassword => EmailEnabled && !string.IsNullOrWhiteSpace(SmtpUsername);
}
