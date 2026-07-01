using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class ConnectionsView : UserControl
{
    private ConnectionsViewModel? _subscribed;

    public ConnectionsView()
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
        if (DataContext is ConnectionsViewModel viewModel)
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

    private void OnSecretInputShouldClear(object? sender, EventArgs e) => PasswordBox.Clear();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Password = box.Password;
        }
    }
}
