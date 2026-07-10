using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.Services;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Runs executed by the background service never notify the app; refresh the visible section
        // when the window regains focus so its data catches up (throttled inside the view model).
        Activated += async (_, _) =>
        {
            if (DataContext is MainViewModel shell)
            {
                await shell.RefreshOnActivationAsync();
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the app kills any in-flight run with it — make that interruption an explicit choice.
        if (App.Services.GetRequiredService<IJobRunCoordinator>().HasActiveRuns
            && !AppDialog.Confirm(this, "Obsync",
                "A sync is still running. Close anyway? The run will be interrupted and marked failed.",
                confirmText: "Close anyway", destructive: true))
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
