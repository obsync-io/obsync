using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class CreateJobWindow : Window
{
    public CreateJobWindow() => InitializeComponent();

    /// <summary>
    /// Opens the Create/Edit Sync Job wizard as a modal dialog. Pass an existing job to edit it.
    /// Returns <c>true</c> when the job was saved.
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window? owner, SyncJob? jobToEdit = null)
    {
        var viewModel = App.Services.GetRequiredService<CreateJobViewModel>();
        await viewModel.LoadAsync();
        if (jobToEdit is not null)
        {
            viewModel.InitializeForEdit(jobToEdit);
        }

        var window = new CreateJobWindow { DataContext = viewModel, Owner = owner };
        viewModel.Saved += (_, _) => window.DialogResult = true;

        return window.ShowDialog() == true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
