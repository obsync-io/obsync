using CommunityToolkit.Mvvm.ComponentModel;
using Obsync.Shared;

namespace Obsync.App.ViewModels;

/// <summary>Application settings and local data locations.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IAsyncViewModel
{
    public string DataRoot => ObsyncPaths.Root;
    public string DatabasePath => ObsyncPaths.DatabasePath;
    public string WorkspacesRoot => ObsyncPaths.WorkspacesRoot;
    public string LogsRoot => ObsyncPaths.LogsRoot;
    public string Version => "Obsync 0.1.0";

    public Task LoadAsync() => Task.CompletedTask;
}
