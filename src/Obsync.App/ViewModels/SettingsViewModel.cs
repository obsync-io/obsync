using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Engine.Alerting;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>One choice in the run-history retention dropdown.</summary>
public sealed record RetentionOption(string Label, int Days);

/// <summary>
/// Application settings, local data locations, the least-privilege SQL permission tool, diagnostics +
/// the support bundle, the proxy configuration, and the audit trail.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IAsyncViewModel
{
    private bool _suppressAutoSave;
    private readonly IAuditWriter _audit;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ISupportBundleWriter _bundle;
    private readonly IAppSettingsRepository _settings;
    private readonly ICredentialStore _credentials;
    private readonly IProxyProvider _proxy;
    private readonly IRunAlertService _alerts;

    public SettingsViewModel(
        IAuditWriter audit,
        IDiagnosticsService diagnostics,
        ISupportBundleWriter bundle,
        IAppSettingsRepository settings,
        ICredentialStore credentials,
        IProxyProvider proxy,
        IRunAlertService alerts)
    {
        _audit = audit;
        _diagnostics = diagnostics;
        _bundle = bundle;
        _settings = settings;
        _credentials = credentials;
        _proxy = proxy;
        _alerts = alerts;
    }

    public string DataRoot => ObsyncPaths.Root;
    public string DatabasePath => ObsyncPaths.DatabasePath;
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
        _suppressAutoSave = true;
        try
        {
            var retentionDays = await _settings.GetRunRetentionDaysAsync();
            var retention = RetentionOptions.FirstOrDefault(o => o.Days == retentionDays);
            if (retention is null)
            {
                // A value set elsewhere (or by an older/newer version) still shows faithfully.
                retention = new RetentionOption($"{retentionDays} days", retentionDays);
                RetentionOptions.Add(retention);
            }

            SelectedRetention = retention;

            var committer = await _settings.GetCommitterAsync();
            CommitterName = committer.Name;
            CommitterEmail = committer.Email ?? string.Empty;

            var workspacesOverride = await _settings.GetWorkspacesRootOverrideAsync();
            WorkspacesRootText = string.IsNullOrWhiteSpace(workspacesOverride)
                ? ObsyncPaths.WorkspacesRoot
                : workspacesOverride;

            NotifyRunFailures = await _settings.GetNotifyRunFailuresAsync();
        }
        finally
        {
            _suppressAutoSave = false;
        }

        ProductionTagsText = string.Join(", ", await _settings.GetProductionTagsAsync());

        var alerts = await _settings.GetAlertSettingsAsync();
        AlertEmailEnabled = alerts.EmailEnabled;
        SmtpHost = alerts.SmtpHost ?? string.Empty;
        SmtpPortText = alerts.SmtpPort.ToString();
        SmtpUseTls = alerts.SmtpUseTls;
        SmtpUsername = alerts.SmtpUsername ?? string.Empty;
        AlertFromAddress = alerts.FromAddress ?? string.Empty;
        AlertToAddresses = alerts.ToAddresses ?? string.Empty;
        AlertWebhookEnabled = alerts.WebhookEnabled;
        AlertWebhookUrl = alerts.WebhookUrl ?? string.Empty;
        AlertOnFailure = alerts.OnFailure;
        AlertOnWarning = alerts.OnWarning;
        AlertOnChanges = alerts.OnChanges;
        AlertScheduledOnly = alerts.ScheduledRunsOnly;
        SmtpPassword = string.Empty;
        SmtpPasswordShouldClear?.Invoke(this, EventArgs.Empty);

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

    // --- Run history retention ------------------------------------------------------------------

    public ObservableCollection<RetentionOption> RetentionOptions { get; } =
    [
        new("Keep forever", 0),
        new("30 days", 30),
        new("90 days", 90),
        new("180 days", 180),
        new("1 year", 365),
    ];

    [ObservableProperty] private RetentionOption? _selectedRetention;
    [ObservableProperty] private string? _retentionStatus;

    [RelayCommand]
    private async Task SaveRetentionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var days = SelectedRetention?.Days ?? 0;
            await _settings.SetRunRetentionDaysAsync(days);
            RetentionStatus = days == 0
                ? "Saved — run history is kept forever."
                : $"Saved — runs older than {days} days are removed automatically (checked daily and at startup).";
        }
        catch (Exception ex)
        {
            RetentionStatus = $"Could not save — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Git committer identity -----------------------------------------------------------------

    [ObservableProperty] private string _committerName = string.Empty;
    [ObservableProperty] private string _committerEmail = string.Empty;
    [ObservableProperty] private string? _committerStatus;

    [RelayCommand]
    private async Task SaveCommitterAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var email = CommitterEmail.Trim();
        if (email.Length > 0 && !email.Contains('@'))
        {
            CommitterStatus = "Enter a valid email address (or leave it blank for the default).";
            return;
        }

        IsBusy = true;
        try
        {
            var name = CommitterName.Trim();
            var committer = new CommitterIdentity(
                name.Length == 0 ? CommitterIdentity.Default.Name : name,
                email.Length == 0 ? null : email);
            await _settings.SetCommitterAsync(committer);
            CommitterName = committer.Name;
            CommitterStatus = $"Saved — sync commits will be authored as {committer.Name}" +
                (committer.Email is null ? "." : $" <{committer.Email}>.");
        }
        catch (Exception ex)
        {
            CommitterStatus = $"Could not save — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Git workspaces location ----------------------------------------------------------------

    [ObservableProperty] private string _workspacesRootText = ObsyncPaths.WorkspacesRoot;
    [ObservableProperty] private string? _workspacesStatus;

    [RelayCommand]
    private void BrowseWorkspacesRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the git workspaces folder" };
        if (dialog.ShowDialog() == true)
        {
            WorkspacesRootText = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task SaveWorkspacesRootAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var path = WorkspacesRootText.Trim();
            if (path.Length == 0 || string.Equals(path, ObsyncPaths.WorkspacesRoot, StringComparison.OrdinalIgnoreCase))
            {
                await _settings.SetWorkspacesRootOverrideAsync(null);
                WorkspacesRootText = ObsyncPaths.WorkspacesRoot;
                WorkspacesStatus = "Saved — using the default location.";
                return;
            }

            if (!System.IO.Path.IsPathRooted(path))
            {
                WorkspacesStatus = @"Enter a full path (for example D:\ObsyncWorkspaces).";
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                WorkspacesStatus = $"Cannot use this folder — {ex.Message}";
                return;
            }

            await _settings.SetWorkspacesRootOverrideAsync(path);
            WorkspacesRootText = path;
            WorkspacesStatus =
                "Saved — repositories clone here from their next run. Files at the old location are not moved or deleted.";
        }
        catch (Exception ex)
        {
            WorkspacesStatus = $"Could not save — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Notifications ---------------------------------------------------------------------------

    [ObservableProperty] private bool _notifyRunFailures = true;
    [ObservableProperty] private string? _notifyStatus;

    // Saves immediately on toggle (no Save button for a single checkbox).
    partial void OnNotifyRunFailuresChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = PersistNotifyAsync(value);
    }

    private async Task PersistNotifyAsync(bool value)
    {
        try
        {
            await _settings.SetNotifyRunFailuresAsync(value);
            NotifyStatus = value
                ? "On — you'll see an in-app notification when a run fails or ends with warnings."
                : "Off — failed runs are still recorded in History, but you won't be notified.";
        }
        catch (Exception ex)
        {
            NotifyStatus = $"Could not save — {ex.Message}";
        }
    }

    // --- Alerts (email + webhook) -----------------------------------------------------------------

    [ObservableProperty] private bool _alertEmailEnabled;
    [ObservableProperty] private string _smtpHost = string.Empty;
    [ObservableProperty] private string _smtpPortText = "587";
    [ObservableProperty] private bool _smtpUseTls = true;
    [ObservableProperty] private string _smtpUsername = string.Empty;
    [ObservableProperty] private string _alertFromAddress = string.Empty;
    [ObservableProperty] private string _alertToAddresses = string.Empty;
    [ObservableProperty] private bool _alertWebhookEnabled;
    [ObservableProperty] private string _alertWebhookUrl = string.Empty;
    [ObservableProperty] private bool _alertOnFailure = true;
    [ObservableProperty] private bool _alertOnWarning = true;
    [ObservableProperty] private bool _alertOnChanges;
    [ObservableProperty] private bool _alertScheduledOnly = true;
    [ObservableProperty] private string? _alertStatus;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string SmtpPassword { get; set; } = string.Empty;

    /// <summary>Raised so the view clears its SMTP-password PasswordBox (after save / reload).</summary>
    public event EventHandler? SmtpPasswordShouldClear;

    [RelayCommand]
    private async Task SaveAlertsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (AlertEmailEnabled && (string.IsNullOrWhiteSpace(SmtpHost)
            || string.IsNullOrWhiteSpace(AlertFromAddress) || string.IsNullOrWhiteSpace(AlertToAddresses)))
        {
            AlertStatus = "Email alerts need an SMTP host, a from address, and at least one recipient.";
            return;
        }

        var port = string.IsNullOrWhiteSpace(SmtpPortText) ? 587 : int.TryParse(SmtpPortText.Trim(), out var parsed) ? parsed : 0;
        if (AlertEmailEnabled && port is < 1 or > 65535)
        {
            AlertStatus = "Enter an SMTP port between 1 and 65535 (587 is the usual submission port).";
            return;
        }

        if (AlertWebhookEnabled
            && (!Uri.TryCreate(AlertWebhookUrl.Trim(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)))
        {
            AlertStatus = "Webhook alerts need a full http(s) URL, e.g. https://example.com/hooks/obsync.";
            return;
        }

        IsBusy = true;
        AlertStatus = "Saving alert settings…";
        try
        {
            var settings = new AlertSettings
            {
                EmailEnabled = AlertEmailEnabled,
                SmtpHost = string.IsNullOrWhiteSpace(SmtpHost) ? null : SmtpHost.Trim(),
                SmtpPort = port,
                SmtpUseTls = SmtpUseTls,
                SmtpUsername = string.IsNullOrWhiteSpace(SmtpUsername) ? null : SmtpUsername.Trim(),
                FromAddress = string.IsNullOrWhiteSpace(AlertFromAddress) ? null : AlertFromAddress.Trim(),
                ToAddresses = string.IsNullOrWhiteSpace(AlertToAddresses) ? null : AlertToAddresses.Trim(),
                WebhookEnabled = AlertWebhookEnabled,
                WebhookUrl = string.IsNullOrWhiteSpace(AlertWebhookUrl) ? null : AlertWebhookUrl.Trim(),
                OnFailure = AlertOnFailure,
                OnWarning = AlertOnWarning,
                OnChanges = AlertOnChanges,
                ScheduledRunsOnly = AlertScheduledOnly,
            };
            await _settings.UpsertAlertSettingsAsync(settings);
            SmtpPortText = settings.SmtpPort.ToString();

            // Store the password when provided; keep the saved one when blank on an authenticated
            // relay (mirrors the proxy pattern); otherwise remove it.
            if (settings.RequiresPassword)
            {
                if (!string.IsNullOrEmpty(SmtpPassword))
                {
                    _credentials.Store(CredentialKeys.SmtpPassword(), SmtpPassword);
                }
            }
            else
            {
                _credentials.Delete(CredentialKeys.SmtpPassword());
            }

            SmtpPassword = string.Empty;
            SmtpPasswordShouldClear?.Invoke(this, EventArgs.Empty);
            AlertStatus = settings.EmailEnabled || settings.WebhookEnabled
                ? "Alert settings saved."
                : "Alert settings saved — no channel is enabled, so no alerts are sent.";
        }
        catch (Exception ex)
        {
            AlertStatus = $"Could not save the alert settings — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendTestAlertAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        AlertStatus = "Sending a test alert…";
        try
        {
            // Tests the SAVED settings — save first if you have just edited them.
            var result = await _alerts.SendTestAsync(CancellationToken.None);
            AlertStatus = result.IsSuccess
                ? "Test alert sent — check the inbox and/or the webhook endpoint."
                : result.Error;
        }
        catch (Exception ex)
        {
            AlertStatus = $"Test failed — {ex.Message}";
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
