using System.Globalization;
using System.Windows.Data;
using Obsync.App.ViewModels;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Converters;

/// <summary>Maps a <see cref="RunTrigger"/> to its short display label (History's Trigger column).</summary>
public sealed class RunTriggerToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RunTrigger.Manual => "Manual",
        RunTrigger.Scheduled => "Scheduled",
        RunTrigger.Startup => "Startup",
        RunTrigger.CatchUp => "Catch-up",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Projects a <see cref="SyncRun"/> to its <see cref="ChangeSplit"/>, so the History grid
/// can feed the same change-token template the timeline uses.</summary>
public sealed class RunToChangeSplitConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SyncRun run ? new ChangeSplit(run.ObjectsAdded, run.ObjectsModified, run.ObjectsDeleted) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a pull request number as its link label, e.g. <c>PR #17</c>.</summary>
public sealed class PullRequestLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int number ? $"PR #{number.ToString(CultureInfo.CurrentCulture)}" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Two-way bridge between the diff viewer's change-type filter (<see cref="ChangeType"/>? — null is
/// "All") and one filter chip's IsChecked. The parameter names the chip ("All", "Added", "Modified",
/// "Deleted"); unchecking returns <see cref="Binding.DoNothing"/> so radio siblings never clear the
/// source when they uncheck.
/// </summary>
public sealed class ChangeTypeFilterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, ParseFilter(parameter));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ParseFilter(parameter) : Binding.DoNothing;

    private static ChangeType? ParseFilter(object? parameter) =>
        parameter is string name && name != "All" ? Enum.Parse<ChangeType>(name) : null;
}
