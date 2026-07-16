using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>The History run actions are gated on a selected run (and, for diffs, a commit),
/// and the timeline projection tracks the shared filters and selection.</summary>
public sealed class HistoryViewModelTests
{
    private static HistoryViewModel NewViewModel(IRunRepository? runs = null)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        return new(
            runs ?? Substitute.For<IRunRepository>(), Substitute.For<IJobRepository>(),
            Substitute.For<IRepositoryProfileRepository>(), Substitute.For<IRunReportWriter>(),
            Substitute.For<IAppSettingsRepository>(), clock, Substitute.For<IJobRunCoordinator>());
    }

    private static IRunRepository RepositoryWith(params SyncRun[] runs)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SyncRun>>([.. runs]));
        return repository;
    }

    private static SyncRun NewRun(string job, int changed = 0, string? commit = null) => new()
    {
        JobName = job,
        RunKey = "20260707-090000",
        Status = changed > 0 ? RunStatus.Succeeded : RunStatus.NoChanges,
        StartedAt = DateTimeOffset.Now,
        ObjectsModified = changed,
        CommitSha = commit,
    };

    [Fact]
    public void ExportReport_IsDisabled_UntilARunIsSelected()
    {
        var vm = NewViewModel();

        Assert.False(vm.ExportReportCommand.CanExecute(null));

        vm.SelectedRun = new SyncRun { JobName = "Prod Sync", RunKey = "20260702-093000" };
        Assert.True(vm.ExportReportCommand.CanExecute(null));

        vm.SelectedRun = null;
        Assert.False(vm.ExportReportCommand.CanExecute(null));
    }

    [Fact]
    public void ViewChanges_IsDisabled_UntilTheSelectedRunHasACommit()
    {
        var vm = NewViewModel();

        Assert.False(vm.ViewChangesCommand.CanExecute(null));

        // A run without a commit (failed, no-changes, or export-only) has nothing to diff.
        vm.SelectedRun = new SyncRun { JobName = "Prod Sync", RunKey = "20260702-093000" };
        Assert.False(vm.ViewChangesCommand.CanExecute(null));

        vm.SelectedRun = new SyncRun { JobName = "Prod Sync", RunKey = "20260702-094500", CommitSha = new string('a', 40) };
        Assert.True(vm.ViewChangesCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_FlagsTheSilentCap_OnlyWhenTheFetchLimitCameBackFull()
    {
        // Exactly the fetch cap (100) — older runs likely exist, so the caption must show.
        var capped = NewViewModel(RepositoryWith([.. Enumerable.Range(0, 100).Select(i => NewRun($"Job {i % 5}"))]));
        await capped.LoadAsync();
        Assert.True(capped.IsCapped);

        // Under the cap — everything is shown, no caption.
        var complete = NewViewModel(RepositoryWith(NewRun("Prod Sync")));
        await complete.LoadAsync();
        Assert.False(complete.IsCapped);
    }

    [Fact]
    public async Task JobFilter_MatchesRunsCaseInsensitively()
    {
        // The dropdown de-dupes names OrdinalIgnoreCase, so "Sales" and "sales" collapse into one
        // entry — the filter must match both spellings or one job's runs silently vanish.
        var vm = NewViewModel(RepositoryWith(NewRun("Sales"), NewRun("sales"), NewRun("Other")));
        await vm.LoadAsync();

        vm.SelectedJob = vm.JobNames.Single(n => string.Equals(n, "sales", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, vm.RunsView.Cast<SyncRun>().Count());
    }

    [Fact]
    public async Task Timeline_IsBuiltOnLoad_AndFollowsTheJobFilter()
    {
        var vm = NewViewModel(RepositoryWith(NewRun("Prod Sync", changed: 3), NewRun("Dev Sync")));

        await vm.LoadAsync();
        var day = Assert.Single(vm.TimelineDays);
        Assert.Equal(2, day.Entries.Count);

        vm.SelectedJob = "Prod Sync";
        day = Assert.Single(vm.TimelineDays);
        var entry = Assert.Single(day.Entries);
        Assert.Equal("Prod Sync", entry.Run.JobName);
    }

    [Fact]
    public async Task ToggleEntry_LoadsTheCappedChangeListOnce_AndNotesTruncation()
    {
        var run = NewRun("Prod Sync", changed: 250, commit: new string('a', 40));
        var repository = RepositoryWith(run);
        repository.GetChangesAsync(run.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ObjectChange>>(
                [.. Enumerable.Range(0, call.ArgAt<int>(1)).Select(i => new ObjectChange
                {
                    Name = $"o{i}", Schema = "dbo", RelativePath = $"views/dbo.o{i}.sql",
                    ObjectType = Obsync.Shared.Objects.SqlObjectType.View, ChangeType = ChangeType.Modified,
                })]));

        var vm = NewViewModel(repository);
        await vm.LoadAsync();
        var entry = vm.TimelineDays[0].Entries[0];

        await vm.ToggleEntryCommand.ExecuteAsync(entry);
        Assert.True(entry.IsExpanded);
        Assert.Equal(100, entry.Changes.Count); // the inline cap
        Assert.Contains("100", entry.TruncationNotice);
        Assert.Contains("250", entry.TruncationNotice);
        Assert.Same(run, vm.SelectedRun); // expanding selects, so the header actions target this run

        // Collapse and re-expand: the changes are not re-fetched.
        await vm.ToggleEntryCommand.ExecuteAsync(entry);
        Assert.False(entry.IsExpanded);
        await vm.ToggleEntryCommand.ExecuteAsync(entry);
        await repository.Received(1).GetChangesAsync(run.Id, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasActiveFilters_TracksEachFilter_AndClearFiltersResetsAllThree()
    {
        var vm = NewViewModel(RepositoryWith(NewRun("Prod Sync", changed: 2), NewRun("Dev Sync")));
        await vm.LoadAsync();

        // Defaults: nothing active, so the "Clear filters" action stays hidden.
        Assert.False(vm.HasActiveFilters);

        vm.SelectedJob = "Prod Sync";
        Assert.True(vm.HasActiveFilters);

        vm.SelectedStatus = vm.StatusOptions.Single(o => o.Status == RunStatus.NoChanges);
        vm.SearchText = "dev";
        Assert.True(vm.HasActiveFilters);
        Assert.Empty(vm.RunsView.Cast<SyncRun>()); // the combined filters match nothing

        vm.ClearFiltersCommand.Execute(null);

        Assert.False(vm.HasActiveFilters);
        Assert.Equal("All jobs", vm.SelectedJob);
        Assert.Null(vm.SelectedStatus.Status);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(2, vm.RunsView.Cast<SyncRun>().Count());
    }

    [Fact]
    public async Task HasActiveFilters_IsTrue_ForEachSingleNonDefaultFilter()
    {
        var vm = NewViewModel(RepositoryWith(NewRun("Prod Sync")));
        await vm.LoadAsync();

        vm.SelectedStatus = vm.StatusOptions.Single(o => o.Status == RunStatus.Failed);
        Assert.True(vm.HasActiveFilters);
        vm.ClearFiltersCommand.Execute(null);

        vm.SearchText = "abc";
        Assert.True(vm.HasActiveFilters);
        vm.ClearFiltersCommand.Execute(null);
        Assert.False(vm.HasActiveFilters);
    }

    [Fact]
    public async Task SelectingARun_HighlightsItsTimelineEntry_InBothDirections()
    {
        var first = NewRun("Prod Sync", changed: 1);
        var second = NewRun("Dev Sync");
        var vm = NewViewModel(RepositoryWith(first, second));
        await vm.LoadAsync();

        var entries = vm.TimelineDays.SelectMany(d => d.Entries).ToList();

        vm.SelectEntryCommand.Execute(entries[0]);
        Assert.Same(entries[0].Run, vm.SelectedRun);
        Assert.True(entries[0].IsSelected);
        Assert.False(entries[1].IsSelected);

        // Grid-side selection flows back into the timeline highlight.
        vm.SelectedRun = entries[1].Run;
        Assert.False(entries[0].IsSelected);
        Assert.True(entries[1].IsSelected);
    }
}
