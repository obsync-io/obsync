using System.Windows;

namespace Obsync.App.Views;

public partial class CreateJobWindow : Window
{
    public CreateJobWindow() => InitializeComponent();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
