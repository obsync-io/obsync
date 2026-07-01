using System.Windows;
using System.Windows.Media;

namespace Obsync.App.Views;

/// <summary>A themed replacement for the system MessageBox: a centered card matching the app design.</summary>
public partial class AppDialog : Window
{
    public AppDialog() => InitializeComponent();

    /// <summary>Shows a confirmation dialog. Returns true when the user confirms.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText = "Confirm", bool destructive = false)
    {
        var dialog = Create(owner, title, message);
        dialog.ConfirmButton.Content = confirmText;
        if (destructive)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows an error dialog with a single OK button.</summary>
    public static void Error(Window? owner, string title, string message)
    {
        var dialog = Create(owner, title, message);
        dialog.ConfirmButton.Content = "OK";
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.SetIcon("IconError", "ErrorBrush", "ErrorSoftBrush");
        dialog.ShowDialog();
    }

    private static AppDialog Create(Window? owner, string title, string message)
    {
        var dialog = new AppDialog { Owner = owner };
        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        return dialog;
    }

    private void SetIcon(string glyphKey, string brushKey, string chipKey)
    {
        IconGlyph.Text = (string)FindResource(glyphKey);
        IconGlyph.Foreground = (Brush)FindResource(brushKey);
        IconChip.Background = (Brush)FindResource(chipKey);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
