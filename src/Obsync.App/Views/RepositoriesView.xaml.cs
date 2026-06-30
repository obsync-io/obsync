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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.SecretInputShouldClear -= OnSecretInputShouldClear;
        }

        _subscribed = DataContext as RepositoriesViewModel;
        if (_subscribed is not null)
        {
            _subscribed.SecretInputShouldClear += OnSecretInputShouldClear;
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
