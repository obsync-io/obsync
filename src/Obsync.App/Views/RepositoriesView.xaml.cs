using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;

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

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RepositoriesViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Token = box.Password;
        }
    }
}
