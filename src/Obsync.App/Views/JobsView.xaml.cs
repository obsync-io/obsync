using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class JobsView : UserControl
{
    public JobsView() => InitializeComponent();

    private async void OnCreateJob(object sender, RoutedEventArgs e)
    {
        await CreateJobWindow.ShowDialogAsync(Window.GetWindow(this));
        await ReloadAsync();
    }

    private async void OnEditJob(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SyncJob job)
        {
            await CreateJobWindow.ShowDialogAsync(Window.GetWindow(this), job);
            await ReloadAsync();
        }
    }

    private async void OnOpenJob(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SyncJob job)
        {
            await App.Services.GetRequiredService<IShellNavigator>().ShowJobDetailAsync(job.Id);
        }
    }

    private async Task ReloadAsync()
    {
        if (DataContext is JobsViewModel jobs)
        {
            await jobs.LoadAsync();
        }
    }
}
