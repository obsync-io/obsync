using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Obsync.Data.Repositories;
using Obsync.Engine.Alerting;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.Engine.Tests;

/// <summary>
/// Delivery-retry behavior of <see cref="RunAlertService"/>: each channel gets exactly one retry,
/// channels fail independently, cancellation skips the retry, and nothing ever throws into the run
/// pipeline. Uses a subclass that fakes the transports (the send methods are virtual for this).
/// </summary>
public sealed class RunAlertServiceRetryTests
{
    /// <summary>Counts attempts per channel; fails an attempt when its predicate says so.</summary>
    private sealed class FakeAlertService : RunAlertService
    {
        private readonly Func<int, bool> _emailAttemptFails;
        private readonly Func<int, bool> _webhookAttemptFails;

        public int EmailAttempts { get; private set; }
        public int WebhookAttempts { get; private set; }

        /// <summary>The settings substitute, so tests can assert what was persisted.</summary>
        public IAppSettingsRepository Settings { get; }

        public FakeAlertService(AlertSettings settings, Func<int, bool> emailAttemptFails, Func<int, bool> webhookAttemptFails)
            : this(SettingsRepository(settings), emailAttemptFails, webhookAttemptFails)
        {
        }

        /// <summary>Builds one over a caller-supplied settings substitute (e.g. one that throws).</summary>
        public static FakeAlertService WithSettings(
            IAppSettingsRepository settings, Func<int, bool> emailAttemptFails, Func<int, bool> webhookAttemptFails) =>
            new(settings, emailAttemptFails, webhookAttemptFails);

        private FakeAlertService(
            IAppSettingsRepository settings, Func<int, bool> emailAttemptFails, Func<int, bool> webhookAttemptFails)
            : base(settings, Substitute.For<ICredentialStore>(), Substitute.For<IProxyProvider>(),
                   NullLogger<RunAlertService>.Instance)
        {
            Settings = settings;
            _emailAttemptFails = emailAttemptFails;
            _webhookAttemptFails = webhookAttemptFails;
        }

        protected override TimeSpan RetryDelay => TimeSpan.Zero;

        protected override Task SendEmailAsync(AlertSettings settings, string subject, string body, CancellationToken cancellationToken)
        {
            EmailAttempts++;
            cancellationToken.ThrowIfCancellationRequested();
            return _emailAttemptFails(EmailAttempts)
                ? Task.FromException(new InvalidOperationException("SMTP relay down"))
                : Task.CompletedTask;
        }

        protected override Task PostWebhookAsync(AlertSettings settings, string json, CancellationToken cancellationToken)
        {
            WebhookAttempts++;
            cancellationToken.ThrowIfCancellationRequested();
            return _webhookAttemptFails(WebhookAttempts)
                ? Task.FromException(new HttpRequestException("endpoint unreachable"))
                : Task.CompletedTask;
        }

        private static IAppSettingsRepository SettingsRepository(AlertSettings settings)
        {
            var repository = Substitute.For<IAppSettingsRepository>();
            repository.GetAlertSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
            return repository;
        }
    }

    private static AlertSettings BothChannels() => new() { EmailEnabled = true, WebhookEnabled = true };

    private static SyncRun FailedRun() => new()
    {
        RunKey = "20260716-090000",
        JobName = "SalesDB sync",
        Status = RunStatus.Failed,
        Trigger = RunTrigger.Scheduled,
        ServerName = "PROD-SQL01",
        Databases = "SalesDB",
        StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task PersistentFailure_RetriesEachChannelExactlyOnce_AndNeverThrows()
    {
        var service = new FakeAlertService(BothChannels(), _ => true, _ => true);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        Assert.Equal(2, service.EmailAttempts);
        Assert.Equal(2, service.WebhookAttempts);
    }

    [Fact]
    public async Task TransientFailure_TheRetryDelivers()
    {
        var service = new FakeAlertService(BothChannels(), attempt => attempt == 1, _ => false);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        Assert.Equal(2, service.EmailAttempts);   // first failed, retry delivered
        Assert.Equal(1, service.WebhookAttempts); // clean sends are not retried
    }

    [Fact]
    public async Task OneChannelFailing_DoesNotStopTheOther()
    {
        var service = new FakeAlertService(BothChannels(), _ => true, _ => false);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        Assert.Equal(2, service.EmailAttempts);
        Assert.Equal(1, service.WebhookAttempts);
    }

    [Fact]
    public async Task APersistentFailure_IsRecordedSoItStopsBeingSilent()
    {
        // Swallowing the failure is correct — an alert must never fail a run. Leaving no trace was
        // not: the only record was a log line, so a service account that cannot read the SMTP
        // password dropped every alert indefinitely while Settings still showed a green test.
        var service = new FakeAlertService(BothChannels(), _ => true, _ => false);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        await service.Settings.Received(1).SetLastAlertFailureAsync(
            Arg.Is<AlertDeliveryFailure>(f =>
                f.Channel == "Email"
                && f.RunKey == "20260716-090000"
                && f.JobName == "SalesDB sync"
                && f.Account.Length > 0
                && f.Error.Contains("SMTP relay down")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASuccessfulSend_ClearsAPreviousFailure()
    {
        // Otherwise the warning would stick on the dashboard forever once alerting recovered.
        var service = new FakeAlertService(BothChannels(), _ => false, _ => false);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        await service.Settings.Received(1).SetLastAlertFailureAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenBothChannelsFail_TheEmailFailureIsTheOneReported()
    {
        // A working webhook must not be able to mask email delivery that has been dead for months.
        var service = new FakeAlertService(BothChannels(), _ => true, _ => true);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        await service.Settings.Received(1).SetLastAlertFailureAsync(
            Arg.Is<AlertDeliveryFailure>(f => f.Channel == "Email"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordingTheOutcome_NeverThrowsIntoTheRun()
    {
        // The bookkeeping is best-effort for the same reason the send is: failing to write down
        // that an alert failed must not fail the run either.
        var settings = Substitute.For<IAppSettingsRepository>();
        settings.GetAlertSettingsAsync(Arg.Any<CancellationToken>()).Returns(BothChannels());
        settings.SetLastAlertFailureAsync(Arg.Any<AlertDeliveryFailure?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("state database is locked")));

        var service = FakeAlertService.WithSettings(settings, _ => true, _ => true);

        await service.NotifyAsync(FailedRun(), CancellationToken.None);

        Assert.Equal(2, service.EmailAttempts);
        Assert.Equal(2, service.WebhookAttempts);
    }

    [Fact]
    public async Task Cancellation_SkipsTheRetry_AndDoesNotThrow()
    {
        var service = new FakeAlertService(BothChannels(), _ => true, _ => true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.NotifyAsync(FailedRun(), cts.Token);

        Assert.Equal(1, service.EmailAttempts);
        Assert.Equal(1, service.WebhookAttempts);
    }
}
