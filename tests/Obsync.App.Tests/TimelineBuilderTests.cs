using Obsync.App.ViewModels;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>The History timeline's day grouping, ordering, labels, and change totals.</summary>
public sealed class TimelineBuilderTests
{
    private static readonly DateTime Today = new(2026, 7, 7);

    private static SyncRun NewRun(DateTime localStart, int added = 0, int modified = 0, int deleted = 0) => new()
    {
        JobName = "Nightly sync",
        RunKey = localStart.ToString("yyyyMMdd-HHmmss"),
        Status = RunStatus.Succeeded,
        StartedAt = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)),
        ObjectsAdded = added,
        ObjectsModified = modified,
        ObjectsDeleted = deleted,
    };

    [Fact]
    public void Build_GroupsByLocalDay_NewestDayAndRunFirst()
    {
        var days = TimelineBuilder.Build(
        [
            NewRun(Today.AddHours(9)),
            NewRun(Today.AddHours(14)),
            NewRun(Today.AddDays(-3).AddHours(2)),
        ], Today);

        Assert.Equal(2, days.Count);
        Assert.Equal(Today, days[0].Date);
        Assert.Equal(2, days[0].Entries.Count);
        // Within a day the newest run comes first.
        Assert.Equal(Today.AddHours(14), days[0].Entries[0].Run.StartedAt.LocalDateTime);
        Assert.Equal(Today.AddDays(-3), days[1].Date);
    }

    [Fact]
    public void Build_SumsTheDaysChangeCounts()
    {
        var days = TimelineBuilder.Build(
        [
            NewRun(Today.AddHours(9), added: 3, modified: 5, deleted: 1),
            NewRun(Today.AddHours(14), added: 2),
        ], Today);

        var day = Assert.Single(days);
        Assert.Equal(new ChangeSplit(Added: 5, Modified: 5, Deleted: 1), day.Split);
        Assert.False(day.Split.HasNoChanges);
        Assert.Equal("2 runs", day.RunsLabel);
    }

    [Fact]
    public void Build_FlagsADayWithoutChanges()
    {
        var days = TimelineBuilder.Build([NewRun(Today.AddHours(9))], Today);

        var day = Assert.Single(days);
        Assert.True(day.Split.HasNoChanges);
        Assert.Equal("1 run", day.RunsLabel);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(-1, "Yesterday")]
    public void DayLabel_UsesFriendlyNamesForTodayAndYesterday(int offsetDays, string expected) =>
        Assert.Equal(expected, TimelineBuilder.DayLabel(Today.AddDays(offsetDays), Today));

    [Fact]
    public void DayLabel_OmitsTheYearOnlyWithinTheCurrentYear()
    {
        // Exact wording is culture-formatted; assert the year presence, not the full string.
        Assert.DoesNotContain("2026", TimelineBuilder.DayLabel(new DateTime(2026, 3, 2), Today));
        Assert.Contains("2025", TimelineBuilder.DayLabel(new DateTime(2025, 12, 30), Today));
    }
}
