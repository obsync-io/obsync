using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// Application settings, local data locations, the least-privilege SQL permission tool, diagnostics +
/// the support bundle, the proxy configuration, and the audit trail.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IAuditWriter _audit;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ISupportBundleWriter _bundle;
    private readonly IAppSettingsRepository _settings;
    private readonly ICredentialStore _credentials;
    private readonly IProxyProvider _proxy;

    public SettingsViewModel(
        IAuditWriter audit,
        IDiagnosticsService diagnostics,
        ISupportBundleWriter bundle,
        IAppSettingsRepository settings,
        ICredentialStore credentials,
        IProxyProvider proxy)
    {
        _audit = audit;
        _diagnostics = diagnostics;
        _bundle = bundle;
        _settings = settings;
        _credentials = credentials;
        _proxy = proxy;
    }

    public string DataRoot => ObsyncPaths.Root;
    public string DatabasePath => ObsyncPaths.DatabasePath;
    public string WorkspacesRoot => ObsyncPaths.WorkspacesRoot;
    public string LogsRoot => ObsyncPaths.LogsRoot;
    public string Version => $"Obsync {VersionInfo.Of(typeof(App).Assembly)}";
    public string EngineVersion => $"Engine {VersionInfo.Of(typeof(Engine.ISyncEngine).Assembly)}";

    /// <summary>The most recent audit-trail entries, newest first.</summary>
    public ObservableCollection<AuditEvent> RecentActivity { get; } = [];

    // --- Diagnostics + support bundle -----------------------------------------------------------

    /// <summary>Results of the most recent diagnostics run (pass/warn/fail rows).</summary>
    public ObservableCollection<DiagnosticResult> Diagnostics { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _diagnosticsSummary;

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        DiagnosticsSummary = "Running diagnostics…";
        try
        {
            await RunDiagnosticsCoreAsync();
        }
        catch (Exception ex)
        {
            DiagnosticsSummary = $"Diagnostics failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportSupportBundleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "obsync-support-bundle.zip",
            Filter = "Zip archive (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".zip",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        DiagnosticsSummary = "Building the support bundle…";
        try
        {
            // Make sure the bundle's diagnostics.json is populated even if the user hasn't run them yet.
            if (Diagnostics.Count == 0)
            {
                await RunDiagnosticsCoreAsync();
            }

            await _bundle.WriteAsync(dialog.FileName, [.. Diagnostics]);
            DiagnosticsSummary = $"Support bundle saved to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            DiagnosticsSummary = $"Could not save the support bundle — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunDiagnosticsCoreAsync()
    {
        var results = await _diagnostics.RunAsync();
        Diagnostics.Clear();
        foreach (var result in results)
        {
            Diagnostics.Add(result);
        }

        var pass = results.Count(r => r.Status == DiagnosticStatus.Pass);
        var warn = results.Count(r => r.Status == DiagnosticStatus.Warning);
        var fail = results.Count(r => r.Status == DiagnosticStatus.Fail);
        DiagnosticsSummary = $"{results.Count} checks — {pass} passed, {warn} warnings, {fail} failed.";
    }

    // --- Required SQL Permissions ---------------------------------------------------------------

    /// <summary>The account (SQL login or <c>DOMAIN\user</c>) the generated grants target.</summary>
    [ObservableProperty] private string _permissionAccount = string.Empty;

    /// <summary>Databases to scope the grants to, one per line (or comma-separated).</summary>
    [ObservableProperty] private string _permissionDatabases = string.Empty;

    [NotifyCanExecuteChangedFor(nameof(CopyPermissionScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(SavePermissionScriptCommand))]
    [ObservableProperty] private string? _permissionScript;

    public async Task LoadAsync()
    {
        ProductionTagsText = string.Join(", ", await _settings.GetProductionTagsAsync());

        var proxy = await _settings.GetProxyAsync();
        SelectedProxyMode = proxy.Mode;
        ProxyUrl = proxy.Url ?? string.Empty;
        ProxyUsername = proxy.Username ?? string.Empty;
        ProxyBypass = string.Join(", ", proxy.BypassHosts);
        ProxyPassword = string.Empty;
        ProxyPasswordShouldClear?.Invoke(this, EventArgs.Empty);

        var events = await _audit.GetRecentAsync(50);
        RecentActivity.Clear();
        foreach (var entry in events)
        {
            RecentActivity.Add(entry);
        }
    }

    // --- Production tags ------------------------------------------------------------------------

    /// <summary>Comma-separated tag words that mark a job as production (arms the Run-Now guard).</summary>
    [ObservableProperty] private string _productionTagsText = string.Empty;

    [ObservableProperty] private string? _productionTagsStatus;

    [RelayCommand]
    private async Task SaveProductionTagsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProductionTagsStatus = "Saving…";
        try
        {
            var markers = JobTags.Parse(ProductionTagsText);
            await _settings.SetProductionTagsAsync(markers);
            ProductionTagsText = string.Join(", ", markers);
            ProductionTagsStatus = markers.Count == 0
                ? "Saved — no tags mark production, so the run guard is off."
                : $"Saved — jobs tagged {string.Join(", ", markers)} are treated as production.";
        }
        catch (Exception ex)
        {
            ProductionTagsStatus = $"Could not save — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Proxy ----------------------------------------------------------------------------------

    [ObservableProperty] private ProxyMode _selectedProxyMode = ProxyMode.None;
    [ObservableProperty] private string _proxyUrl = string.Empty;
    [ObservableProperty] private string _proxyUsername = string.Empty;
    [ObservableProperty] private string _proxyBypass = string.Empty;
    [ObservableProperty] private string? _proxyStatus;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string ProxyPassword { get; set; } = string.Empty;

    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues<ProxyMode>();

    /// <summary>True for Manual mode — reveals the URL / credentials / bypass inputs.</summary>
    public bool IsManualProxy => SelectedProxyMode == ProxyMode.Manual;

    /// <summary>Raised so the view clears its proxy-password PasswordBox (after save / reload).</summary>
    public event EventHandler? ProxyPasswordShouldClear;

    partial void OnSelectedProxyModeChanged(ProxyMode value) => OnPropertyChanged(nameof(IsManualProxy));

    [RelayCommand]
    private async Task SaveProxyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProxyStatus = "Saving proxy settings…";
        try
        {
            var settings = new ProxySettings
            {
                Mode = SelectedProxyMode,
                Url = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl.Trim(),
                Username = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim(),
                BypassHosts = [.. ProxyBypass
                    .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            };
            await _settings.UpsertProxyAsync(settings);

            // Store the password when provided; keep the saved one when blank on an authenticated
            // manual proxy (mirrors the server/repo edit pattern); otherwise remove it.
            if (settings.RequiresPassword)
            {
                if (!string.IsNullOrEmpty(ProxyPassword))
                {
                    _credentials.Store(CredentialKeys.Proxy(), ProxyPassword);
                }
            }
            else
            {
                _credentials.Delete(CredentialKeys.Proxy());
            }

            ProxyPassword = string.Empty;
            ProxyPasswordShouldClear?.Invoke(this, EventArgs.Empty);
            ProxyStatus = "Proxy settings saved.";
        }
        catch (Exception ex)
        {
            ProxyStatus = $"Could not save the proxy settings — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProxyStatus = "Testing the connection to GitHub…";
        try
        {
            // Tests the SAVED settings — save first if you have just edited them.
            var result = await _proxy.TestAsync();
            ProxyStatus = result.IsSuccess
                ? "Reachable — GitHub responded through the current proxy setting."
                : result.Error;
        }
        catch (Exception ex)
        {
            ProxyStatus = $"Test failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GeneratePermissionScript()
    {
        var account = PermissionAccount.Trim();
        if (string.IsNullOrEmpty(account))
        {
            PermissionScript = "-- Enter the account name first.";
            return;
        }

        var databases = PermissionDatabases
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (databases.Count == 0)
        {
            PermissionScript = "-- Enter at least one database name.";
            return;
        }

        PermissionScript = SqlPermissionScriptBuilder.Build(account, databases);
    }

    private bool HasScript => !string.IsNullOrWhiteSpace(PermissionScript);

    [RelayCommand(CanExecute = nameof(HasScript))]
    private void CopyPermissionScript()
    {
        if (PermissionScript is { } script)
        {
            System.Windows.Clipboard.SetText(script);
        }
    }

    [RelayCommand(CanExecute = nameof(HasScript))]
    private void SavePermissionScript()
    {
        if (PermissionScript is not { } script)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "obsync-permissions.sql",
            Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*",
            DefaultExt = ".sql",
        };

        if (dialog.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dialog.FileName, script);
        }
    }
}
