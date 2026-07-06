using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Obsync.Shared;

namespace Obsync.App.Tests;

/// <summary>
/// A headless WPF smoke test: on an STA thread it constructs the real <see cref="Obsync.App.App"/>
/// (loading the full resource graph — theme + converters + templates), asserts the key design-system
/// resources resolve, and then measures/arranges every screen so that control templates are applied.
/// Template application is where WPF surfaces errors like an unset <c>Foreground</c> or a broken
/// resource reference, so this reproduces view-load crashes that a plain build cannot catch.
/// </summary>
[Collection("wpf application")]
public sealed class DesignSystemTests
{
    private static readonly string[] ExpectedKeys =
    [
        "AccentBrush", "CardBrush", "BorderBrush", "PanelBrush", "SuccessSoftBrush", "AccentSoftBrush",
        "DiffAddedEmphasisBrush", "DiffDeletedEmphasisBrush",
        "AppFontFamily", "IconFontFamily", "MonoFontFamily", "IconDiff", "IconCopy", "IconImport", "IconClose",
        "RadiusCard", "RadiusPill", "CardPadding",
        "PageTitle", "SectionTitle", "MetricValue", "Body", "BodyStrong", "Muted", "Caption", "Icon",
        "Card", "Panel", "Divider", "PrimaryButton", "SecondaryButton", "SubtleButton",
        "IconButton", "LinkButton", "NavButton",
        // App-level resources (converters + reusable templates)
        "StatusToBrush", "MsToDuration", "StatusBadgeTemplate", "ChangeBadgeTemplate",
    ];

    [Fact]
    public void App_LoadsResourcesAndRendersEveryView()
    {
        Exception? fatal = null;
        var missing = new List<string>();
        var renderFailures = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? CreateApp();

                foreach (var key in ExpectedKeys)
                {
                    if (app.TryFindResource(key) is null)
                    {
                        missing.Add(key);
                    }
                }

                foreach (var (name, create) in Views())
                {
                    try
                    {
                        var view = create();
                        // Rooting the view under a Window connects it to Application.Resources for
                        // StaticResource lookups (as MainWindow does at runtime). No Show() — that
                        // would need a desktop; construction + layout is enough to apply templates.
                        _ = new Window { Width = 1280, Height = 880, Content = view };
                        view.Measure(new Size(1280, 880));
                        view.Arrange(new Rect(0, 0, 1280, 880));
                        view.UpdateLayout();
                    }
                    catch (Exception ex)
                    {
                        var chain = new List<string>();
                        for (var e = ex; e is not null; e = e.InnerException)
                        {
                            chain.Add($"{e.GetType().Name}: {e.Message}");
                        }

                        renderFailures.Add($"{name}: {string.Join(" <-- ", chain)}");
                    }
                }
            }
            catch (Exception ex)
            {
                fatal = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(fatal is null, $"WPF initialization failed: {fatal}");
        Assert.True(missing.Count == 0, $"Missing design-system resources: {string.Join(", ", missing)}");
        Assert.True(renderFailures.Count == 0, $"View render failures:\n{string.Join("\n", renderFailures)}");
    }

    [Fact]
    public void StatusBadge_RendersLabel_ForNullStatus()
    {
        string? renderedText = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? CreateApp();
                var template = (DataTemplate)app.FindResource("StatusBadgeTemplate");
                var content = new ContentControl { Content = (RunStatus?)null, ContentTemplate = template };
                _ = new Window { Width = 200, Height = 100, Content = content };
                content.Measure(new Size(200, 100));
                content.Arrange(new Rect(0, 0, 200, 100));
                content.UpdateLayout();
                renderedText = FirstTextBlockText(content);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(error is null, $"Rendering the null-status badge threw: {error}");
        // A never-run job has a null RunStatus. The badge must still render a "Not run" label —
        // this asserts the behavior so a regression (blank status cell) is caught.
        Assert.Equal("Not run", renderedText);
    }

    private static string? FirstTextBlockText(DependencyObject root)
    {
        if (root is TextBlock { Text.Length: > 0 } tb)
        {
            return tb.Text;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FirstTextBlockText(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void BrandIcon_ResourceLoadsFromPack()
    {
        Exception? error = null;
        var pixelWidth = 0;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? CreateApp();
                var uri = new Uri("pack://application:,,,/Obsync.App;component/Assets/Obsync_Icon.png", UriKind.Absolute);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage(uri);
                pixelWidth = bitmap.PixelWidth; // forces decode — throws if the resource is missing
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(error is null, $"Loading the brand icon resource threw: {error}");
        Assert.True(pixelWidth > 0, "The brand icon resource decoded to zero width.");
    }

    internal static Application CreateApp()
    {
        var app = new Obsync.App.App();
        app.InitializeComponent();
        return app;
    }

    private static IEnumerable<(string Name, Func<UIElement> Create)> Views() =>
    [
        ("DashboardView", () => new Obsync.App.Views.DashboardView()),
        ("JobsView", () => new Obsync.App.Views.JobsView()),
        ("JobDetailView", () => new Obsync.App.Views.JobDetailView()),
        ("ServersView", () => new Obsync.App.Views.ServersView()),
        ("RepositoriesView", () => new Obsync.App.Views.RepositoriesView()),
        ("HistoryView", () => new Obsync.App.Views.HistoryView()),
        ("SettingsView", () => new Obsync.App.Views.SettingsView()),
    ];
}
