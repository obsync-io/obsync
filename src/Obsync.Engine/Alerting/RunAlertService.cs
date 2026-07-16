using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;

namespace Obsync.Engine.Alerting;

/// <inheritdoc cref="IRunAlertService" />
public class RunAlertService : IRunAlertService
{
    /// <summary>Hard cap per attempt so an unreachable relay or endpoint never stalls a run.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Pause before the single per-channel retry; virtual so tests can collapse it.</summary>
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(5);

    private readonly IAppSettingsRepository _settings;
    private readonly ICredentialStore _credentials;
    private readonly IProxyProvider _proxy;
    private readonly ILogger<RunAlertService> _logger;

    public RunAlertService(
        IAppSettingsRepository settings, ICredentialStore credentials, IProxyProvider proxy, ILogger<RunAlertService> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _proxy = proxy;
        _logger = logger;
    }

    public async Task NotifyAsync(SyncRun run, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAlertSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!RunAlertEvaluator.ShouldAlert(settings, run))
        {
            return;
        }

        if (settings.EmailEnabled)
        {
            await SendWithOneRetryAsync(
                "Email", ct => SendEmailAsync(settings, RunAlertPayload.BuildEmailSubject(run), RunAlertPayload.BuildEmailBody(run), ct),
                run, cancellationToken).ConfigureAwait(false);
        }

        if (settings.WebhookEnabled)
        {
            await SendWithOneRetryAsync(
                "Webhook", ct => PostWebhookAsync(settings, RunAlertPayload.BuildWebhookJson(run), ct),
                run, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends on one channel with a single retry after <see cref="RetryDelay"/>. Delivery failures
    /// are logged and swallowed — an alert must never fail or delay a run beyond the bounded
    /// timeout+retry — and only host cancellation skips the retry (a per-attempt timeout is an
    /// ordinary failure, so it IS retried).
    /// </summary>
    private async Task SendWithOneRetryAsync(
        string channel, Func<CancellationToken, Task> send, SyncRun run, CancellationToken cancellationToken)
    {
        try
        {
            await send(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down — nothing to retry, nothing to log.
        }
        catch (Exception)
        {
            try
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                await send(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is shutting down mid-retry.
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(
                    retryEx, "{Channel} alert for run {RunKey} ({JobName}) failed after one retry.",
                    channel, run.RunKey, run.JobName);
            }
        }
    }

    public async Task<Result> SendTestAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAlertSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.EmailEnabled && !settings.WebhookEnabled)
        {
            return Result.Failure("No alert channel is enabled — turn on email or webhook alerts and save first.");
        }

        string? firstError = null;

        if (settings.EmailEnabled)
        {
            try
            {
                await SendEmailAsync(
                    settings, "[Obsync] Test alert",
                    "This is a test alert from Obsync. Email alerts are configured correctly.", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstError = $"Email failed — {ex.Message}";
            }
        }

        if (settings.WebhookEnabled)
        {
            try
            {
                await PostWebhookAsync(
                    settings, """{"event":"test","message":"This is a test alert from Obsync."}""", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstError ??= $"Webhook failed — {ex.Message}";
            }
        }

        return firstError is null ? Result.Success() : Result.Failure(firstError);
    }

    /// <summary>One email delivery attempt; virtual so tests can fake the transport.</summary>
    protected virtual async Task SendEmailAsync(AlertSettings settings, string subject, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost)
            || string.IsNullOrWhiteSpace(settings.FromAddress)
            || string.IsNullOrWhiteSpace(settings.ToAddresses))
        {
            throw new InvalidOperationException(
                "Email alerts need an SMTP host, a from address, and at least one recipient.");
        }

        // The async SmtpClient API has no send timeout of its own, so the cap rides the token.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);

        // System.Net.Mail.SmtpClient is unfashionable, but for a desktop tool's best-effort
        // plain-SMTP submission it is the pragmatic, dependency-free choice.
        using var client = new SmtpClient(settings.SmtpHost.Trim(), settings.SmtpPort) { EnableSsl = settings.SmtpUseTls };
        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(
                settings.SmtpUsername.Trim(), _credentials.Retrieve(CredentialKeys.SmtpPassword()) ?? string.Empty);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress.Trim()),
            Subject = subject,
            Body = body,
        };
        foreach (var address in settings.ToAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(address);
        }

        await client.SendMailAsync(message, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>One webhook delivery attempt; virtual so tests can fake the transport.</summary>
    protected virtual async Task PostWebhookAsync(AlertSettings settings, string json, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Webhook alerts need an absolute http(s) URL.");
        }

        // Honor the configured proxy exactly like the other outbound HTTP paths.
        var resolution = await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false);
        using var handler = new HttpClientHandler { Proxy = resolution?.WebProxy, UseProxy = resolution is not null };
        using var http = new HttpClient(handler) { Timeout = SendTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Obsync");

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
