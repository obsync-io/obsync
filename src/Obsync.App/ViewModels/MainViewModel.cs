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

    /// <summary>
    /// Releases a Job Workspace view model when navigation moves away from it.
    /// </summary>
    /// <remarks>
    /// Cleanup cannot depend on the view's Unloaded event. Both the outgoing and incoming workspace
    /// resolve to the SAME DataTemplate, so WPF's ContentPresenter reuses the existing view and only
    /// swaps its DataContext — Unloaded never fires, and the previous view model stays subscribed to
    /// the run-state object that the run coordinator keeps alive for the life of the process.
    /// <para>
    /// Each leaked view model retains its loaded runs, changes and logs, and keeps reacting to run
    /// notifications with background reloads nobody can see. The path is ordinary: open one job's
    /// workspace, then click a failure toast for another. Unloaded still fires when the workspace is
    /// left for a rail section, so this covers the case it cannot.
    /// </para>
    /// </remarks>
    partial void OnCurrentViewChanged(object? oldValue, object? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue) && oldValue is JobDetailViewModel previous)
        {
            previous.DetachRunState();
        }
    }

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

            var result = await _services.GetRequiredService<IUpdateChecker>().CheckAsync();

            // Stamped AFTER the call, and only when it actually reached GitHub. Stamping first meant
            // any failure — a laptop offline at login, a proxy hiccup, or a shared egress IP that
            // had spent the unauthenticated 60-per-hour budget — cost the machine its entire daily
            // window. Behind one NAT, a site large enough to hit that limit during the morning login
            // peak starved the same machines every day, and they never learned about an update. The
            // check runs once per app launch and CheckAsync never throws, so retrying on the next
            // launch cannot storm.
            if (result.Error is null)
            {
                await settings.SetLastUpdateCheckAsync(now);
            }

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
                Message = $"You're on {VersionInfo.Of(typeof(App).Assembly)}. {UpgradeGuidance.ShortNotice}",
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
