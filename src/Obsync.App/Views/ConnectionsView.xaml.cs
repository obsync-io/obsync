using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Password = box.Password;
        }
    }
}
