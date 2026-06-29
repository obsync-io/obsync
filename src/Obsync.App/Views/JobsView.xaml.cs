using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class JobsView : UserControl
{
    public JobsView() => InitializeComponent();

    private async void OnCreateJob(object sender, RoutedEventArgs e)
    {
        var viewModel = App.Services.GetRequiredService<CreateJobViewModel>();
        await viewModel.LoadAsync();

        var window = new CreateJobWindow { DataContext = viewModel, Owner = Window.GetWindow(this) };
        viewModel.Saved += (_, _) => window.DialogResult = true;

        window.ShowDialog();

        if (DataContext is JobsViewModel jobs)
        {
            await jobs.LoadAsync();
        }
    }
}
