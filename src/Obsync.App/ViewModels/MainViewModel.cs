using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Obsync.App.ViewModels;

/// <summary>The shell view model: drives left-rail navigation between the section view models.</summary>
public sealed partial class MainViewModel : ObservableObject, IShellNavigator
{
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private object? _currentView;

    // Section view models are resolved lazily from the container rather than injected, so that a
    // section that depends on IShellNavigator (e.g. the dashboard's "Open job") does not form a
    // construction-time cycle with this view model. The first navigation is triggered after
    // construction (see InitializeAsync) for the same reason.
    public MainViewModel(IServiceProvider services) => _services = services;

    /// <summary>Shows the initial section. Called once after the shell is constructed and shown.</summary>
    public Task InitializeAsync() => NavigateAsync("Dashboard");

    [RelayCommand]
    private async Task NavigateAsync(string section)
    {
        CurrentView = section switch
        {
            "Jobs" => _services.GetRequiredService<JobsViewModel>(),
            "Connections" => _services.GetRequiredService<ConnectionsViewModel>(),
            "Repositories" => _services.GetRequiredService<RepositoriesViewModel>(),
            "History" => _services.GetRequiredService<HistoryViewModel>(),
            "Settings" => _services.GetRequiredService<SettingsViewModel>(),
            _ => _services.GetRequiredService<DashboardViewModel>(),
        };

        if (CurrentView is IAsyncViewModel asyncViewModel)
        {
            await asyncViewModel.LoadAsync();
        }
    }

    public Task ShowSectionAsync(string section) => NavigateAsync(section);

    public async Task ShowJobDetailAsync(Guid jobId)
    {
        var detail = _services.GetRequiredService<JobDetailViewModel>();
        await detail.LoadAsync(jobId);
        CurrentView = detail;
    }
}
