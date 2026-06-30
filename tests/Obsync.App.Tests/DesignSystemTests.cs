using System.Windows;

namespace Obsync.App.Tests;

/// <summary>
/// Loads the split design-system ResourceDictionary on an STA thread. Because WPF resolves a
/// dictionary's <c>StaticResource</c> references at load time, this throws if any cross-file
/// reference (Controls → Colors/Typography/Icons) is broken — catching missing-resource errors
/// that would otherwise only surface as a crash when the app starts on a real desktop.
/// </summary>
public sealed class DesignSystemTests
{
    private static readonly string[] ExpectedKeys =
    [
        "AccentBrush", "CardBrush", "BorderBrush", "PanelBrush", "SuccessSoftBrush", "AccentSoftBrush",
        "AppFontFamily", "IconFontFamily", "CardShadow", "RadiusCard", "RadiusPill", "CardPadding",
        "PageTitle", "SectionTitle", "MetricValue", "Body", "BodyStrong", "Muted", "Caption", "Icon",
        "Card", "Panel", "Divider", "PrimaryButton", "SecondaryButton", "SubtleButton",
        "IconButton", "LinkButton", "NavButton",
    ];

    [Fact]
    public void Theme_LoadsAndResolvesAllReferences()
    {
        Exception? error = null;
        var missing = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                // An Application registers the "application://" pack scheme used by the Source URI.
                if (Application.Current is null)
                {
                    _ = new Application();
                }

                var dictionary = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Obsync.App;component/Themes/Theme.xaml", UriKind.Absolute),
                };

                foreach (var key in ExpectedKeys)
                {
                    if (Find(dictionary, key) is null)
                    {
                        missing.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(error is null, $"Loading the design system threw: {error}");
        Assert.True(missing.Count == 0, $"Missing design-system resources: {string.Join(", ", missing)}");
    }

    private static object? Find(ResourceDictionary dictionary, string key)
    {
        if (dictionary.Contains(key))
        {
            return dictionary[key];
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
