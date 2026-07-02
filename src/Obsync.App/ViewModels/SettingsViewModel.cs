using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Application settings, local data locations, the least-privilege SQL permission tool, and the audit trail.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IAuditWriter _audit;

    public SettingsViewModel(IAuditWriter audit) => _audit = audit;

    public string DataRoot => ObsyncPaths.Root;
    public string DatabasePath => ObsyncPaths.DatabasePath;
    public string WorkspacesRoot => ObsyncPaths.WorkspacesRoot;
    public string LogsRoot => ObsyncPaths.LogsRoot;
    public string Version => "Obsync 0.1.0";

    /// <summary>The most recent audit-trail entries, newest first.</summary>
    public ObservableCollection<AuditEvent> RecentActivity { get; } = [];

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
