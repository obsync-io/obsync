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

    private SettingsViewModel NewViewModel() => new(
        Substitute.For<IAuditWriter>(),
        Substitute.For<IDiagnosticsService>(),
        Substitute.For<ISupportBundleWriter>(),
        _settings,
        _credentials,
        Substitute.For<IProxyProvider>(),
        Substitute.For<IRunAlertService>(),
        Substitute.For<IUpdateChecker>());

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
}
