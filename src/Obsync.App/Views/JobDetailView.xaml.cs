using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class JobDetailView : UserControl
{
    public JobDetailView() => InitializeComponent();

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (DataContext is JobDetailViewModel { Job: { } job } detail)
        {
            var saved = await CreateJobWindow.ShowDialogAsync(Window.GetWindow(this), job);
            if (saved)
            {
                await detail.LoadAsync(job.Id);
            }
        }
    }
}
