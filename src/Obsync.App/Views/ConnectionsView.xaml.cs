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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.SecretInputShouldClear -= OnSecretInputShouldClear;
        }

        _subscribed = DataContext as ConnectionsViewModel;
        if (_subscribed is not null)
        {
            _subscribed.SecretInputShouldClear += OnSecretInputShouldClear;
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
