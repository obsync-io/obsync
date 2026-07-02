using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class CreateJobWindow : Window
{
    private static readonly Regex NonDigit = new(@"[^0-9]", RegexOptions.Compiled);
    private static readonly Regex NonTime = new(@"[^0-9:]", RegexOptions.Compiled);

    public CreateJobWindow() => InitializeComponent();

    // --- Numeric / time input guards: reject characters that can't form a valid value so invalid
    //     text is never silently dropped later during parsing. -------------------------------------
    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = NonDigit.IsMatch(e.Text);

    private void OnTimeInput(object sender, TextCompositionEventArgs e) => e.Handled = NonTime.IsMatch(e.Text);

    private static void FilterPaste(DataObjectPastingEventArgs e, Regex reject)
    {
        if (e.DataObject.GetData(typeof(string)) is string text && reject.IsMatch(text))
        {
            e.CancelCommand();
        }
    }

    private void OnPasteDigitsOnly(object sender, DataObjectPastingEventArgs e) =>
        FilterPaste(e, NonDigit);

    private void OnPasteTime(object sender, DataObjectPastingEventArgs e) =>
        FilterPaste(e, NonTime);

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
