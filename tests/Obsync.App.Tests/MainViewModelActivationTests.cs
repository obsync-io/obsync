using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Engine.Alerting;
using Obsync.Shared.Abstractions;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The window-activation refresh reloads the visible section — except Settings, whose LoadAsync
/// resets every unsaved field and clears the typed SMTP/proxy passwords. Alt-tabbing away and back
/// must never wipe in-progress Settings input.
/// </summary>
public sealed class MainViewModelActivationTests
{
    private static SettingsViewModel NewSettingsViewModel(IAppSettingsRepository settings) => new(
        Substitute.For<IAuditWriter>(),
        Substitute.For<IDiagnosticsService>(),
        Substitute.For<ISupportBundleWriter>(),
        settings,
        Substitute.For<ICredentialStore>(),
        Substitute.For<IProxyProvider>(),
        Substitute.For<IRunAlertService>(),
        Substitute.For<IUpdateChecker>());

    [Fact]
    public async Task RefreshOnActivation_SkipsTheSettingsSection()
    {
        var settings = Substitute.For<IAppSettingsRepository>();
        var main = new MainViewModel(Substitute.For<IServiceProvider>())
        {
            CurrentView = NewSettingsViewModel(settings),
        };

        await main.RefreshOnActivationAsync();

        // Settings' LoadAsync starts by reading the retention setting; it must never have run.
        await settings.DidNotReceive().GetRunRetentionDaysAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshOnActivation_StillReloadsOtherSections()
    {
        var section = Substitute.For<IAsyncViewModel>();
        var main = new MainViewModel(Substitute.For<IServiceProvider>()) { CurrentView = section };

        await main.RefreshOnActivationAsync();

        await section.Received(1).LoadAsync();
    }
}
