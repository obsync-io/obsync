using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class AddServerWindow : Window
{
    public AddServerWindow() => InitializeComponent();

    /// <summary>Opens the Add/Edit Server dialog. Pass an existing server to edit it. Returns true when saved.</summary>
    public static Task<bool> ShowDialogAsync(Window? owner, SqlConnectionProfile? serverToEdit = null)
    {
        var viewModel = App.Services.GetRequiredService<ServerDialogViewModel>();
        if (serverToEdit is not null)
        {
            viewModel.LoadForEdit(serverToEdit);
        }
        else if (LocalServerDetector.Detect() is { } localServer)
        {
            // Pre-fill a detected local SQL Server so the common case is one click.
            viewModel.ServerName = localServer;
        }

        var window = new AddServerWindow { DataContext = viewModel, Owner = owner };
        viewModel.Saved += (_, _) => window.DialogResult = true;

        return Task.FromResult(window.ShowDialog() == true);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerDialogViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Password = box.Password;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
