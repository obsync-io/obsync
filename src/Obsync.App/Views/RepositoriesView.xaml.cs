using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class RepositoriesView : UserControl
{
    public RepositoriesView() => InitializeComponent();

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RepositoriesViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Token = box.Password;
        }
    }
}
