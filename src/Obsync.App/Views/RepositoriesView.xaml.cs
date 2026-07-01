using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class RepositoriesView : UserControl
{
    private RepositoriesViewModel? _subscribed;

    public RepositoriesView()
    {
        InitializeComponent();

        // Subscribe on Loaded / unsubscribe on Unloaded so the (singleton) view model does not
        // retain this recreated-per-navigation view — and its secret-bearing PasswordBox — after
        // the user navigates away.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        if (DataContext is RepositoriesViewModel viewModel)
        {
            _subscribed = viewModel;
            viewModel.SecretInputShouldClear += OnSecretInputShouldClear;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.SecretInputShouldClear -= OnSecretInputShouldClear;
            _subscribed = null;
        }
    }

    private void OnSecretInputShouldClear(object? sender, EventArgs e) => TokenBox.Clear();

    private void OnDeleteRepo(object sender, RoutedEventArgs e)
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

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RepositoriesViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Token = box.Password;
        }
    }
}
