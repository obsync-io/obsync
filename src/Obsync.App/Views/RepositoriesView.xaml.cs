using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class RepositoriesView : UserControl
{
    public RepositoriesView() => InitializeComponent();

    private async void OnAddRepository(object sender, RoutedEventArgs e)
    {
        await AddRepositoryWindow.ShowDialogAsync(Window.GetWindow(this));
        await ReloadAsync();
    }

    private async void OnEditRepository(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GitRepositoryProfile repository)
        {
            await AddRepositoryWindow.ShowDialogAsync(Window.GetWindow(this), repository);
            await ReloadAsync();
        }
    }

    private void OnDeleteRepository(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GitRepositoryProfile repository && DataContext is RepositoriesViewModel viewModel)
        {
            if (AppDialog.Confirm(Window.GetWindow(this), "Delete repository",
                $"Delete the repository “{repository.Name}”? Sync jobs that use it will need a different repository.",
                "Delete", destructive: true))
            {
                viewModel.DeleteCommand.Execute(repository);
            }
        }
    }

    private async Task ReloadAsync()
    {
        if (DataContext is RepositoriesViewModel viewModel)
        {
            await viewModel.LoadAsync();
        }
    }
}
