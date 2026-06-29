using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Obsync.Shared;

namespace Obsync.App.Converters;

/// <summary>Maps a <see cref="RunStatus"/> to its status colour.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            RunStatus.Succeeded => "SuccessBrush",
            RunStatus.NoChanges => "SuccessBrush",
            RunStatus.Warning => "WarningBrush",
            RunStatus.Failed => "ErrorBrush",
            RunStatus.Cancelled => "TextMutedBrush",
            RunStatus.Running => "AccentBrush",
            _ => "TextMutedBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null → Collapsed, non-null → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
