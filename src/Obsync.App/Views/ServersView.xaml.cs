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

    private void OnCopyServerName(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SqlConnectionProfile server && DataContext is ServersViewModel viewModel)
        {
            try
            {
                Clipboard.SetText(server.ServerName);
                viewModel.StatusMessage = $"Copied “{server.ServerName}” to the clipboard.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another process is holding the clipboard open — a transient Windows condition.
                viewModel.StatusMessage = "Could not access the clipboard — try again.";
            }
        }
    }

    private void OnDeleteServer(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SqlConnectionProfile server && DataContext is ServersViewModel viewModel)
        {
            if (AppDialog.Confirm(Window.GetWindow(this), "Delete server",
                $"Delete the server “{server.Name}”? Sync jobs that use it will need a different server.",
                "Delete", destructive: true))
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
