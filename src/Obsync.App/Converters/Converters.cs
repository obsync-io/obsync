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

/// <summary>Maps a <see cref="RunStatus"/> (or null) to its soft badge background brush.</summary>
public sealed class StatusToBadgeBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            RunStatus.Succeeded or RunStatus.NoChanges => "SuccessSoftBrush",
            RunStatus.Warning => "WarningSoftBrush",
            RunStatus.Failed => "ErrorSoftBrush",
            RunStatus.Running => "AccentSoftBrush",
            _ => "NeutralSoftBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="RunStatus"/> (or null) to a friendly label for status badges.</summary>
public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RunStatus.Succeeded => "Succeeded",
        RunStatus.NoChanges => "No changes",
        RunStatus.Warning => "Warning",
        RunStatus.Failed => "Failed",
        RunStatus.Running => "Running",
        RunStatus.Pending => "Pending",
        RunStatus.Cancelled => "Cancelled",
        _ => "Not run",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an <see cref="ChangeType"/> to its strong accent brush (Added/Modified/Deleted).</summary>
public sealed class ChangeTypeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ChangeType.Added => "SuccessBrush",
            ChangeType.Modified => "WarningBrush",
            ChangeType.Deleted => "ErrorBrush",
            _ => "TextMutedBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an <see cref="ChangeType"/> to its soft badge background brush.</summary>
public sealed class ChangeTypeToBadgeBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ChangeType.Added => "SuccessSoftBrush",
            ChangeType.Modified => "WarningSoftBrush",
            ChangeType.Deleted => "ErrorSoftBrush",
            _ => "NeutralSoftBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ConnectionTestStatus"/> to a friendly label.</summary>
public sealed class ConnectionStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionTestStatus.Connected => "Connected",
        ConnectionTestStatus.Failed => "Failed",
        _ => "Not tested",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ConnectionTestStatus"/> to its strong status brush.</summary>
public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ConnectionTestStatus.Connected => "SuccessBrush",
            ConnectionTestStatus.Failed => "ErrorBrush",
            _ => "TextMutedBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ConnectionTestStatus"/> to its soft badge background brush.</summary>
public sealed class ConnectionStatusToBadgeBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ConnectionTestStatus.Connected => "SuccessSoftBrush",
            ConnectionTestStatus.Failed => "ErrorSoftBrush",
            _ => "NeutralSoftBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns an enum value into friendly display text: <c>WindowsIntegrated</c> → "Windows
/// Integrated", <c>ProgrammabilityOnly</c> → "Programmability Only", <c>SqlLogin</c> → "SQL Login".</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var text = value.ToString() ?? string.Empty;
        var spaced = System.Text.RegularExpressions.Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return spaced.Replace("Sql", "SQL", StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a duration in milliseconds as <c>hh:mm:ss</c>.</summary>
public sealed class MsToDurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L,
        };

        return TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
