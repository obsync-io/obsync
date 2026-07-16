using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The shell view model: drives left-rail navigation between the section view models and hosts
/// the in-app notification toasts.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IShellNavigator
{
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(12);

    /// <summary>Minimum gap between activation-driven refreshes — activation fires on every focus switch.</summary>
    private static readonly TimeSpan ActivationRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private DateTimeOffset _lastActivationRefresh;

    [ObservableProperty]
    private object? _currentView;

    // The active top-level section, bound by each nav-rail RadioButton (via SectionToBool) so the
    // highlight always tracks the shown page — including programmatic navigation and job drill-down.
    [ObservableProperty]
    private string _currentSection = "Dashboard";

    /// <summary>Navigation rail collapsed to icons only. Persisted across sessions.</summary>
    [ObservableProperty]
    private bool _isNavCollapsed;

    // Section view models are resolved lazily from the container rather than injected, so that a
    // section that depends on IShellNavigator (e.g. the dashboard's "Open job") does not form a
    // construction-time cycle with this view model. The first navigation is triggered after
    // construction (see InitializeAsync) for the same reason.
    public MainViewModel(IServiceProvider services) => _services = services;

    /// <summary>In-app notifications, newest last, rendered bottom-right by the shell window.</summary>
    public ObservableCollection<ToastItem> Toasts { get; } = [];

    /// <summary>Shows the initial section. Called once after the shell is constructed and shown.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            IsNavCollapsed = await _services.GetRequiredService<IAppSettingsRepository>().GetNavCollapsedAsync();
        }
        catch (Exception)
        {
            // A cosmetic preference must never block startup; default to expanded.
        }

        await NavigateAsync("Dashboard");

        // Resolved lazily (not ctor-injected) to keep this shell view model cycle-free — see the
        // note on the constructor.
        _services.GetRequiredService<IJobRunCoordinator>().RunCompleted += OnRunCompleted;
        await ShowMissedFailuresAsync();
        await ShowAvailableUpdateAsync();
    }

    /// <summary>Collapses/expands the navigation rail; the preference persists across sessions.</summary>
    [RelayCommand]
    private async Task ToggleNavAsync()
    {
        IsNavCollapsed = !IsNavCollapsed;
        try
        {
            await _services.GetRequiredService<IAppSettingsRepository>().SetNavCollapsedAsync(IsNavCollapsed);
        }
        catch (Exception)
        {
            // Best-effort persistence; the in-session toggle already applied.
        }
    }

    /// <summary>
    /// Reloads the visible section when the window regains focus, so runs executed by the background
    /// service show up without navigating away and back. Called from the shell window's Activated.
    /// Re-navigating would be a no-op (NavigateAsync dedupes on the same view instance), so the
    /// section's LoadAsync is invoked directly.
    /// </summary>
    public async Task RefreshOnActivationAsync()
    {
        // Settings has no service-run-fed data to catch up on, and its LoadAsync resets every
        // unsaved field (including the typed SMTP/proxy passwords) — alt-tabbing must not wipe them.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastActivationRefresh < ActivationRefreshInterval
            || CurrentView is SettingsViewModel
            || CurrentView is not IAsyncViewModel section)
        {
            return;
        }

        _lastActivationRefresh = now;
        try
        {
            await section.LoadAsync();
        }
        catch (Exception)
        {
            // A focus-driven refresh is best-effort; user-driven loads surface their own errors.
        }
    }

    // A run the app itself executed just finished: surface failures/warnings as a toast.
    private async void OnRunCompleted(object? sender, SyncRun run)
    {
        try
        {
            if (run.Status is not (RunStatus.Failed or RunStatus.Warning)
                || !await _services.GetRequiredService<IAppSettingsRepository>().GetNotifyRunFailuresAsync())
            {
                return;
            }

            ShowToast(new ToastItem
            {
                Title = run.Status == RunStatus.Failed
                    ? $"Run failed — {run.JobName}"
                    : $"Run finished with warnings — {run.JobName}",
                Message = FirstLine(run.ErrorMessage)
                    ?? (run.Status == RunStatus.Failed
                        ? "Open the job for the full error."
                        : "Some items were skipped — open the job for details."),
                IsError = run.Status == RunStatus.Failed,
                JobId = run.JobId,
            });
        }
        catch (Exception)
        {
            // A notification must never take the shell down.
        }
    }

    // Scheduled (service) runs fail without the app open; summarize anything missed since the
    // last session so unattended failures can't go unnoticed.
    private async Task ShowMissedFailuresAsync()
    {
        try
        {
            var settings = _services.GetRequiredService<IAppSettingsRepository>();
            var now = DateTimeOffset.UtcNow;
            if (await settings.GetLastFailureCheckAsync() is { } since
                && await settings.GetNotifyRunFailuresAsync())
            {
                var failures = await _services.GetRequiredService<IRunRepository>()
                    .CountUnattendedFailuresSinceAsync(since);
                if (failures > 0)
                {
                    ShowToast(new ToastItem
                    {
                        Title = failures == 1
                            ? "A scheduled run failed while you were away"
                            : $"{failures} scheduled runs failed while you were away",
                        Message = "Open History to see which runs need attention.",
                        IsError = true,
                    });
                }
            }

            await settings.SetLastFailureCheckAsync(now);
        }
        catch (Exception)
        {
            // Never block startup over the notification check.
        }
    }

    // A newer GitHub release gets one calm accent toast, at most once per version; the check runs
    // at most once per 24h and any failure (offline, private repo, rate limit) shows nothing.
    private async Task ShowAvailableUpdateAsync()
    {
        try
        {
            var settings = _services.GetRequiredService<IAppSettingsRepository>();
            var now = DateTimeOffset.UtcNow;
            if (await settings.GetLastUpdateCheckAsync() is { } last && now - last < TimeSpan.FromHours(24))
            {
                return;
            }

            await settings.SetLastUpdateCheckAsync(now);
            var result = await _services.GetRequiredService<IUpdateChecker>().CheckAsync();
            if (!result.IsUpdateAvailable
                || result.LatestVersion is not { } version
                || version == await settings.GetLastNotifiedUpdateVersionAsync())
            {
                return;
            }

            await settings.SetLastNotifiedUpdateVersionAsync(version);
            ShowToast(new ToastItem
            {
                Title = $"Obsync {version} is available",
                Message = $"You're on {VersionInfo.Of(typeof(App).Assembly)}. Open the release notes to download the update.",
                IsInfo = true,
                Url = result.ReleaseUrl,
                ActionText = "View release",
            });
        }
        catch (Exception)
        {
            // The update check is best-effort; startup never depends on github.com being reachable.
        }
    }

    private void ShowToast(ToastItem toast)
    {
        Toasts.Add(toast);
        var timer = new DispatcherTimer { Interval = ToastLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }

    [RelayCommand]
    private void DismissToast(ToastItem toast) => Toasts.Remove(toast);

    [RelayCommand]
    private async Task OpenToastAsync(ToastItem toast)
    {
        Toasts.Remove(toast);
        if (toast.Url is { } url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (toast.JobId is { } jobId)
        {
            await ShowJobDetailAsync(jobId, CurrentSection);
        }
        else
        {
            await NavigateAsync("History");
        }
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.AsSpan().Trim();
        var newline = line.IndexOfAny('\r', '\n');
        return (newline < 0 ? line : line[..newline]).ToString();
    }

    // True while navigation code itself updates CurrentSection, so the change hook below only
    // reacts to EXTERNAL writes — the rail's two-way IsChecked binding, i.e. assistive-tech or
    // programmatic selection, which previously moved the highlight without changing the page.
    // A real click also fires NavigateCommand; the ReferenceEquals guard in NavigateAsync
    // collapses that click+binding pair into a single navigation.
    private bool _syncingSection;

    partial void OnCurrentSectionChanged(string value)
    {
        if (!_syncingSection)
        {
            _ = NavigateAsync(value);
        }
    }

    [RelayCommand]
    private async Task NavigateAsync(string section)
    {
        var target = ResolveSection(section);
        if (ReferenceEquals(CurrentView, target))
        {
            return;
        }

        CurrentView = target;
        SetSection(section);

        if (CurrentView is IAsyncViewModel asyncViewModel)
        {
            await asyncViewModel.LoadAsync();
        }
    }

    private object ResolveSection(string section) => section switch
    {
        "Jobs" => _services.GetRequiredService<JobsViewModel>(),
        "Servers" => _services.GetRequiredService<ServersViewModel>(),
        "Repositories" => _services.GetRequiredService<RepositoriesViewModel>(),
        "History" => _services.GetRequiredService<HistoryViewModel>(),
        "Settings" => _services.GetRequiredService<SettingsViewModel>(),
        _ => _services.GetRequiredService<DashboardViewModel>(),
    };

    private void SetSection(string section)
    {
        _syncingSection = true;
        CurrentSection = section;
        _syncingSection = false;
    }

    public Task ShowSectionAsync(string section) => NavigateAsync(section);

    public async Task ShowJobDetailAsync(Guid jobId, string origin = "Jobs")
    {
        var detail = _services.GetRequiredService<JobDetailViewModel>();
        detail.OriginSection = origin;
        await detail.LoadAsync(jobId);
        // The job workspace is not a rail item; keep the originating section highlighted so the rail
        // stays consistent and "Back" returns to where the drill-down started.
        SetSection(origin);
        CurrentView = detail;
    }
}
