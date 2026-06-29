using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Obsync.App.ViewModels;

/// <summary>The shell view model: owns the section view models and drives left-rail navigation.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboard;
    private readonly JobsViewModel _jobs;
    private readonly ConnectionsViewModel _connections;
    private readonly RepositoriesViewModel _repositories;
    private readonly HistoryViewModel _history;
    private readonly SettingsViewModel _settings;

    [ObservableProperty]
    private object? _currentView;

    public MainViewModel(
        DashboardViewModel dashboard,
        JobsViewModel jobs,
        ConnectionsViewModel connections,
        RepositoriesViewModel repositories,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        _dashboard = dashboard;
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _history = history;
        _settings = settings;

        _ = NavigateAsync("Dashboard");
    }

    [RelayCommand]
    private async Task NavigateAsync(string section)
    {
        CurrentView = section switch
        {
            "Jobs" => _jobs,
            "Connections" => _connections,
            "Repositories" => _repositories,
            "History" => _history,
            "Settings" => _settings,
            _ => _dashboard,
        };

        if (CurrentView is IAsyncViewModel asyncViewModel)
        {
            await asyncViewModel.LoadAsync();
        }
    }
}
