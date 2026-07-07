using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The in-app notification cards live in MainWindow (not a UserControl), so the view smoke test
/// doesn't cover them. This probe realizes the toast ItemsControl with real items and asserts the
/// template builds — catching missing resources or bad bindings that only fail at runtime.
/// </summary>
[Collection("wpf application")]
public sealed class ToastRenderTests
{
    private sealed class ToastHost
    {
        public ObservableCollection<ToastItem> Toasts { get; } =
        [
            new ToastItem { Title = "Run failed — Scripts", Message = "Login failed for user 'obsync'.", IsError = true, JobId = Guid.NewGuid() },
            new ToastItem { Title = "Run finished with warnings — Scripts", Message = "2 items were skipped.", IsError = false },
            new ToastItem
            {
                Title = "Obsync 9.9.9 is available",
                Message = "You're on 0.4.0. Open the release notes to download the update.",
                IsInfo = true,
                Url = "https://github.com/obsync/obsync/releases/latest",
                ActionText = "View release",
            },
        ];
    }

    [Fact]
    public void MainWindow_RendersToastCards()
    {
        Exception? error = null;
        var texts = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? DesignSystemTests.CreateApp();
                var window = new MainWindow { DataContext = new ToastHost() };
                // A window never shown doesn't build its visual tree, but its CONTENT can be laid
                // out directly (it stays parented to the window for resource/DataContext lookups).
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1280, 880));
                root.Arrange(new Rect(0, 0, 1280, 880));
                root.UpdateLayout();
                CollectText(root, texts);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(error is null, $"Rendering the toast overlay threw: {error}");
        Assert.Contains("Run failed — Scripts", texts);
        Assert.Contains("Run finished with warnings — Scripts", texts);
        Assert.Contains("View details", texts);
        // The info template path (accent styling + custom action text) must build too.
        Assert.Contains("Obsync 9.9.9 is available", texts);
        Assert.Contains("View release", texts);
    }

    private static void CollectText(DependencyObject root, List<string> texts)
    {
        if (root is TextBlock { Text.Length: > 0 } textBlock)
        {
            texts.Add(textBlock.Text);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CollectText(VisualTreeHelper.GetChild(root, i), texts);
        }
    }
}
