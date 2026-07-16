using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The +added / ~modified / −deleted summary of a run or a day, rendered everywhere as the same
/// coloured tokens (History grid, timeline entries, timeline day totals) via one shared template.
/// </summary>
public sealed record ChangeSplit(int Added, int Modified, int Deleted)
{
    /// <summary>True when nothing changed — the token template shows a muted dash instead.</summary>
    public bool HasNoChanges => Added == 0 && Modified == 0 && Deleted == 0;
}

/// <summary>One run inside the History timeline, with its lazily loaded object changes.</summary>
public sealed partial class TimelineEntry : ObservableObject
{
    public TimelineEntry(SyncRun run)
    {
        Run = run;
        Split = new ChangeSplit(run.ObjectsAdded, run.ObjectsModified, run.ObjectsDeleted);
    }

    public SyncRun Run { get; }

    /// <summary>The run's change counts in token form.</summary>
    public ChangeSplit Split { get; }

    /// <summary>Local wall-clock time of the run, e.g. "2:05 PM".</summary>
    public string TimeLabel => Run.StartedAt.LocalDateTime.ToString("t");

    /// <summary>Context line under the job name: the databases scanned and who triggered the run.</summary>
    public string ContextLabel => string.IsNullOrWhiteSpace(Run.TriggeredBy)
        ? Run.Databases
        : $"{Run.Databases} · {Run.TriggeredBy}";

    public bool CanExpand => Run.ChangeCount > 0;

    public bool CanDiff => Run.CommitSha is not null;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLoadingChanges;

    /// <summary>Set after loading when the run has more changes than the inline cap shows.</summary>
    [ObservableProperty] private string? _truncationNotice;

    /// <summary>The capped change list, loaded on first expand.</summary>
    public ObservableCollection<TimelineChange> Changes { get; } = [];

    /// <summary>Whether <see cref="Changes"/> has been fetched (expanding again never re-queries).</summary>
    public bool ChangesLoaded { get; set; }
}

/// <summary>A changed object row in an expanded timeline entry (carries its entry for click-through).</summary>
public sealed record TimelineChange(TimelineEntry Entry, ObjectChange Change);

/// <summary>One calendar day in the History timeline, newest runs first.</summary>
public sealed class TimelineDay
{
    public TimelineDay(DateTime date, string dateLabel, IReadOnlyList<TimelineEntry> entries)
    {
        Date = date;
        DateLabel = dateLabel;
        Entries = entries;
        Split = new ChangeSplit(
            entries.Sum(e => e.Run.ObjectsAdded),
            entries.Sum(e => e.Run.ObjectsModified),
            entries.Sum(e => e.Run.ObjectsDeleted));
    }

    public DateTime Date { get; }

    public string DateLabel { get; }

    public IReadOnlyList<TimelineEntry> Entries { get; }

    /// <summary>The day's summed change counts in token form.</summary>
    public ChangeSplit Split { get; }

    public string RunsLabel => Entries.Count == 1 ? "1 run" : $"{Entries.Count} runs";
}

/// <summary>Groups runs into timeline days. Pure, so the grouping and labels are unit-tested directly.</summary>
internal static class TimelineBuilder
{
    /// <summary>Builds the day groups, newest day first, preserving the given (newest-first) run order.</summary>
    internal static IReadOnlyList<TimelineDay> Build(IEnumerable<SyncRun> runs, DateTime localToday)
    {
        return
        [
            .. runs
                .GroupBy(r => r.StartedAt.LocalDateTime.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new TimelineDay(
                    g.Key,
                    DayLabel(g.Key, localToday),
                    [.. g.OrderByDescending(r => r.StartedAt).Select(r => new TimelineEntry(r))])),
        ];
    }

    internal static string DayLabel(DateTime date, DateTime today)
    {
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return date.Year == today.Year ? date.ToString("dddd, MMMM d") : date.ToString("dddd, MMMM d, yyyy");
    }
}
