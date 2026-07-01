using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private async void OnCreateJob(object sender, RoutedEventArgs e)
    {
        await CreateJobWindow.ShowDialogAsync(Window.GetWindow(this));
        if (DataContext is DashboardViewModel dashboard)
        {
            await dashboard.LoadAsync();
        }
    }

    // Getting-started shortcuts: navigate to another section (button Tag = section name).
    private async void OnGoToSection(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string section)
        {
            await App.Services.GetRequiredService<IShellNavigator>().ShowSectionAsync(section);
        }
    }
}
