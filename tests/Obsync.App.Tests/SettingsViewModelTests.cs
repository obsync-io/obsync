using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Engine.Alerting;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The Settings save paths must honor the "leave blank to keep the saved one" labels: merely
/// disabling a channel never deletes its stored secret. And a manual proxy is refused without a
/// usable URL — a bad one would silently resolve to a direct connection while the UI says Manual.
/// </summary>
public sealed class SettingsViewModelTests
{
    private readonly IAppSettingsRepository _settings = Substitute.For<IAppSettingsRepository>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private SettingsViewModel NewViewModel() => new(
        _audit,
        Substitute.For<IDiagnosticsService>(),
        Substitute.For<ISupportBundleWriter>(),
        _settings,
        _credentials,
        Substitute.For<IProxyProvider>(),
        Substitute.For<IRunAlertService>(),
        Substitute.For<IUpdateChecker>(),
        Substitute.For<ILogFileReader>(),
        Substitute.For<ISupportInfoService>());

    [Fact]
    public async Task SaveAlerts_KeepsTheStoredSmtpPassword_WhenEmailAlertsAreDisabled()
    {
        // A stored password exists (saved while email was on); the user turns email off.
        var vm = NewViewModel();
        vm.AlertEmailEnabled = false;
        vm.SmtpUsername = "relay-user";

        await vm.SaveAlertsCommand.ExecuteAsync(null);

        await _settings.Received(1).UpsertAlertSettingsAsync(Arg.Any<AlertSettings>(), Arg.Any<CancellationToken>());
        _credentials.DidNotReceive().Delete(Arg.Any<string>());
        _credentials.DidNotReceive().Store(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SaveProxy_KeepsTheStoredPassword_WhenTheModeLeavesManual()
    {
        var vm = NewViewModel();
        vm.SelectedProxyMode = ProxyMode.None;

        await vm.SaveProxyCommand.ExecuteAsync(null);

        await _settings.Received(1).UpsertProxyAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>());
        _credentials.DidNotReceive().Delete(Arg.Any<string>());
    }

    [Fact]
    public async Task SaveProxy_RejectsManualMode_WithoutAValidHttpUrl()
    {
        var vm = NewViewModel();
        vm.SelectedProxyMode = ProxyMode.Manual;
        vm.ProxyUrl = string.Empty;

        await vm.SaveProxyCommand.ExecuteAsync(null);

        // Nothing persisted, and the status explains what a manual proxy needs.
        await _settings.DidNotReceive().UpsertProxyAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>());
        Assert.Contains("http", vm.ProxyStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveProxy_RejectsManualMode_WithANonHttpUrl()
    {
        var vm = NewViewModel();
        vm.SelectedProxyMode = ProxyMode.Manual;
        vm.ProxyUrl = "proxy.corp:8080"; // no scheme — not an absolute http(s) URL

        await vm.SaveProxyCommand.ExecuteAsync(null);

        await _settings.DidNotReceive().UpsertProxyAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAlerts_RejectsAnInvalidSmtpPort_EvenWhileEmailIsDisabled()
    {
        // Saving would otherwise persist port 0 that surfaces later, when email is enabled.
        var vm = NewViewModel();
        vm.AlertEmailEnabled = false;
        vm.SmtpPortText = "not-a-port";

        await vm.SaveAlertsCommand.ExecuteAsync(null);

        await _settings.DidNotReceive().UpsertAlertSettingsAsync(Arg.Any<AlertSettings>(), Arg.Any<CancellationToken>());
        Assert.Contains("port", vm.AlertStatus, StringComparison.OrdinalIgnoreCase);
    }

    // --- Audit completeness -------------------------------------------------------------------------

    [Fact]
    public async Task SaveProductionTags_WritesOneSettingsChangedEvent_WithTheSectionNameOnly()
    {
        var vm = NewViewModel();
        vm.ProductionTagsText = "prod, live";

        await vm.SaveProductionTagsCommand.ExecuteAsync(null);

        await _audit.Received(1).WriteAsync(
            AuditAction.SettingsChanged, "Settings", null, "Production tags", "Production tags", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAlerts_AuditsCredentialsUpdated_OnlyWhenAPasswordWasActuallySaved()
    {
        var vm = NewViewModel();
        vm.AlertEmailEnabled = false;

        await vm.SaveAlertsCommand.ExecuteAsync(null);

        // No password typed: the section save is audited, a credential update is not.
        await _audit.Received(1).WriteAsync(
            AuditAction.SettingsChanged, "Settings", null, "Alerts", "Alerts", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(
            AuditAction.CredentialsUpdated, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());

        vm.SmtpPassword = "s3cret";
        await vm.SaveAlertsCommand.ExecuteAsync(null);

        // The event names WHICH credential changed — never its value.
        await _audit.Received(1).WriteAsync(
            AuditAction.CredentialsUpdated, "Settings", null, "SMTP password", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveProxy_AuditsCredentialsUpdated_WhenAProxyPasswordWasSaved()
    {
        var vm = NewViewModel();
        vm.SelectedProxyMode = ProxyMode.None;
        vm.ProxyPassword = "s3cret";

        await vm.SaveProxyCommand.ExecuteAsync(null);

        await _audit.Received(1).WriteAsync(
            AuditAction.CredentialsUpdated, "Settings", null, "Proxy password", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePermissionScript_AuditsGrantVsRevoke_WithAccountAndDatabaseCount()
    {
        var vm = NewViewModel();
        vm.PermissionAccount = "svc_obsync";
        vm.PermissionDatabases = "SalesDB\nHRDB";

        await vm.GeneratePermissionScriptCommand.ExecuteAsync(null);
        await vm.GenerateRevokeScriptCommand.ExecuteAsync(null);

        await _audit.Received(1).WriteAsync(
            AuditAction.PermissionScriptGenerated, "PermissionScript", null, "svc_obsync",
            "Grant · 2 database(s)", Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            AuditAction.PermissionScriptGenerated, "PermissionScript", null, "svc_obsync",
            "Revoke · 2 database(s)", Arg.Any<CancellationToken>());
        Assert.Equal("Revoke script", vm.PermissionScriptLabel); // the preview labels what it shows
        Assert.Contains("REVOKE CONNECT FROM [svc_obsync];", vm.PermissionScript);
    }

    [Fact]
    public async Task GeneratePermissionScript_WithInvalidInput_DoesNotAudit()
    {
        var vm = NewViewModel();
        vm.PermissionAccount = string.Empty;

        await vm.GeneratePermissionScriptCommand.ExecuteAsync(null);

        await _audit.DidNotReceive().WriteAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Null(vm.PermissionScriptLabel);
    }

    [Fact]
    public async Task CheckForUpdates_AuditsTheOutcome_WithoutUrls()
    {
        var updates = Substitute.For<IUpdateChecker>();
        updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            new UpdateCheckResult(IsUpdateAvailable: true, LatestVersion: "9.9.9", ReleaseUrl: "https://example.com/r", Error: null));
        var vm = new SettingsViewModel(
            _audit,
            Substitute.For<IDiagnosticsService>(),
            Substitute.For<ISupportBundleWriter>(),
            _settings,
            _credentials,
            Substitute.For<IProxyProvider>(),
            Substitute.For<IRunAlertService>(),
            updates,
            Substitute.For<ILogFileReader>(),
            Substitute.For<ISupportInfoService>());

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        await _audit.Received(1).WriteAsync(
            AuditAction.UpdateChecked, "Application", Arg.Is((string?)null), Arg.Is((string?)null),
            Arg.Is<string>(d => d.Contains("Update available") && !d.Contains("http")), Arg.Any<CancellationToken>());
    }

    // --- Logs panel filtering -----------------------------------------------------------------------

    [Fact]
    public async Task LogsPanel_FiltersBySeverityAndSearch()
    {
        var logs = Substitute.For<ILogFileReader>();
        logs.ReadRecentAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<LogEntry>>(
        [
            new(DateTimeOffset.UtcNow, LogSeverity.Error, "ERR", "push failed for job Alpha", "app"),
            new(DateTimeOffset.UtcNow, LogSeverity.Warning, "WRN", "slow query", "service"),
            new(DateTimeOffset.UtcNow, LogSeverity.Information, "INF", "run finished for job Alpha", "app"),
        ]);
        var vm = new SettingsViewModel(
            _audit,
            Substitute.For<IDiagnosticsService>(),
            Substitute.For<ISupportBundleWriter>(),
            _settings,
            _credentials,
            Substitute.For<IProxyProvider>(),
            Substitute.For<IRunAlertService>(),
            Substitute.For<IUpdateChecker>(),
            logs,
            Substitute.For<ISupportInfoService>());

        await vm.EnsureLogsLoadedAsync();
        Assert.Equal(3, vm.FilteredLogEntries.Count);

        vm.SelectedLogSeverity = "Error";
        Assert.Single(vm.FilteredLogEntries);
        Assert.Equal("push failed for job Alpha", vm.FilteredLogEntries[0].Message);

        vm.SelectedLogSeverity = "All";
        vm.LogSearchText = "job alpha"; // case-insensitive message search
        Assert.Equal(2, vm.FilteredLogEntries.Count);

        // A second tab visit does not re-read the files (load on demand, refresh is explicit).
        await vm.EnsureLogsLoadedAsync();
        await logs.Received(1).ReadRecentAsync(Arg.Any<CancellationToken>());
    }
}
