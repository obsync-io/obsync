using System.Globalization;
using System.Windows.Data;
using Obsync.App.Converters;
using Obsync.App.ViewModels;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>The History/diff display converters: trigger humanization, the run → change-split
/// projection, the PR link label, and the change-type chip bridge.</summary>
public sealed class RunDisplayConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(RunTrigger.Manual, "Manual")]
    [InlineData(RunTrigger.Scheduled, "Scheduled")]
    [InlineData(RunTrigger.Startup, "Startup")]
    [InlineData(RunTrigger.CatchUp, "Catch-up")]
    public void RunTrigger_HumanizesEveryValue(RunTrigger trigger, string expected) =>
        Assert.Equal(expected, new RunTriggerToTextConverter().Convert(trigger, typeof(string), null, Culture));

    [Fact]
    public void RunToChangeSplit_ProjectsTheRunsCounts()
    {
        var run = new SyncRun { JobName = "j", RunKey = "k", ObjectsAdded = 3, ObjectsModified = 12, ObjectsDeleted = 1 };

        var split = Assert.IsType<ChangeSplit>(new RunToChangeSplitConverter().Convert(run, typeof(object), null, Culture));

        Assert.Equal(new ChangeSplit(Added: 3, Modified: 12, Deleted: 1), split);
        Assert.False(split.HasNoChanges);
        Assert.True(new ChangeSplit(0, 0, 0).HasNoChanges);
    }

    [Fact]
    public void PullRequestLabel_FormatsTheNumber_AndBlanksNull()
    {
        var converter = new PullRequestLabelConverter();

        Assert.Equal("PR #17", converter.Convert(17, typeof(string), null, Culture));
        Assert.Equal(string.Empty, converter.Convert(null, typeof(string), null, Culture));
    }

    [Fact]
    public void ChangeTypeFilter_ChecksTheMatchingChip_AndUncheckingNeverClearsTheSource()
    {
        var converter = new ChangeTypeFilterConverter();

        // null = the "All" chip.
        Assert.Equal(true, converter.Convert(null, typeof(bool), "All", Culture));
        Assert.Equal(false, converter.Convert(null, typeof(bool), "Modified", Culture));
        Assert.Equal(true, converter.Convert(ChangeType.Modified, typeof(bool), "Modified", Culture));

        // Checking a chip writes its type; a sibling unchecking must not overwrite it.
        Assert.Equal(ChangeType.Deleted, converter.ConvertBack(true, typeof(object), "Deleted", Culture));
        Assert.Null(converter.ConvertBack(true, typeof(object), "All", Culture));
        Assert.Equal(Binding.DoNothing, converter.ConvertBack(false, typeof(object), "Added", Culture));
    }
}
