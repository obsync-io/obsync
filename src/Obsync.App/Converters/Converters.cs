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

/// <summary>Maps a <see cref="ViewModels.DiffRowKind"/> to its line-background tint.</summary>
public sealed class DiffRowKindToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ViewModels.DiffRowKind.Added => "SuccessSoftBrush",
            ViewModels.DiffRowKind.Deleted => "ErrorSoftBrush",
            ViewModels.DiffRowKind.Imaginary => "NeutralSoftBrush",
            _ => null,
        };

        return key is null ? Brushes.Transparent : Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
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

/// <summary>Converts a <see cref="DateTimeOffset"/> (or nullable) to the user's local time, formatted
/// with the general "g" pattern. Returns an empty string for null so grids show a blank cell rather
/// than a raw UTC value. Used everywhere timestamps appear so the UI is consistently local time.</summary>
public sealed class LocalDateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset dto => dto.LocalDateTime.ToString("g", culture),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Builds the tooltip for an "Overdue" next-run indicator from the job's cached next-run
/// time (<see cref="DateTimeOffset"/>): the scheduled local time plus the corrective hint. Kept as
/// a converter so the tables and the Job Workspace header phrase the explanation identically.</summary>
public sealed class OverdueDetailConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset dto =>
            $"Scheduled for {dto.LocalDateTime.ToString("g", culture)} — the run has not started. Check the Obsync service.",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a collection count to visibility: a positive count is Visible, zero/null Collapsed.
/// Used to hide a DataGrid (and its header row) when the list is empty so the empty-state panel is
/// the only thing shown.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns true when the bound section name equals the <c>ConverterParameter</c>. Drives the
/// nav-rail highlight from <c>MainViewModel.CurrentSection</c>. <see cref="ConvertBack"/> returns the
/// parameter when a rail item becomes checked (and <see cref="Binding.DoNothing"/> otherwise) so the
/// two-way binding never clears the source when siblings uncheck.</summary>
public sealed class SectionToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
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
