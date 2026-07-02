using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// Application settings, local data locations, the least-privilege SQL permission tool, diagnostics +
/// the support bundle, and the audit trail.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IAuditWriter _audit;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ISupportBundleWriter _bundle;

    public SettingsViewModel(IAuditWriter audit, IDiagnosticsService diagnostics, ISupportBundleWriter bundle)
    {
        _audit = audit;
        _diagnostics = diagnostics;
        _bundle = bundle;
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
        var events = await _audit.GetRecentAsync(50);
        RecentActivity.Clear();
        foreach (var entry in events)
        {
            RecentActivity.Add(entry);
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
