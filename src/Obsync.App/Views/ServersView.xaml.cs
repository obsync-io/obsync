using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private async void OnAddServer(object sender, RoutedEventArgs e)
    {
        await AddServerWindow.ShowDialogAsync(Window.GetWindow(this));
        await ReloadAsync();
    }

    private async void OnEditServer(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SqlConnectionProfile server)
        {
            await AddServerWindow.ShowDialogAsync(Window.GetWindow(this), server);
            await ReloadAsync();
        }
    }

    private void OnDeleteServer(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SqlConnectionProfile server && DataContext is ServersViewModel viewModel)
        {
            var confirm = MessageBox.Show(
                $"Delete the server “{server.Name}”? Sync jobs that use it will need a different server.",
                "Delete server", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.OK)
            {
                viewModel.DeleteCommand.Execute(server);
            }
        }
    }

    private async Task ReloadAsync()
    {
        if (DataContext is ServersViewModel viewModel)
        {
            await viewModel.LoadAsync();
        }
    }
}
