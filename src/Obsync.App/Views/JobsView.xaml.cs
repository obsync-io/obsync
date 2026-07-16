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
            await App.Services.GetRequiredService<IShellNavigator>().ShowJobDetailAsync(job.Id, "Jobs");
        }
    }

    // Opens the row's overflow ("⋯") menu on left click, anchored under the button. The menu
    // inherits the row's SyncJob DataContext through its PlacementTarget.
    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnDuplicateJob(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SyncJob job && DataContext is JobsViewModel jobs)
        {
            jobs.DuplicateCommand.Execute(job);
        }
    }

    private void OnExportJob(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SyncJob job && DataContext is JobsViewModel jobs)
        {
            jobs.ExportConfigCommand.Execute(job);
        }
    }

    private void OnDeleteJob(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SyncJob job && DataContext is JobsViewModel jobs)
        {
            if (AppDialog.Confirm(Window.GetWindow(this), "Delete sync job",
                $"Delete the sync job “{job.Name}”? This removes the job and its run history. The server and repository are not affected.",
                "Delete", destructive: true))
            {
                jobs.DeleteCommand.Execute(job);
            }
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
