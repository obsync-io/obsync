using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class AddRepositoryWindow : Window
{
    public AddRepositoryWindow() => InitializeComponent();

    /// <summary>Opens the Add/Edit Repository dialog. Pass an existing repository to edit it. Returns true when saved.</summary>
    public static Task<bool> ShowDialogAsync(Window? owner, GitRepositoryProfile? repositoryToEdit = null)
    {
        var viewModel = App.Services.GetRequiredService<RepositoryDialogViewModel>();
        if (repositoryToEdit is not null)
        {
            viewModel.LoadForEdit(repositoryToEdit);
        }

        var window = new AddRepositoryWindow { DataContext = viewModel, Owner = owner };
        viewModel.Saved += (_, _) => window.DialogResult = true;

        return Task.FromResult(window.ShowDialog() == true);
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RepositoryDialogViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Token = box.Password;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
