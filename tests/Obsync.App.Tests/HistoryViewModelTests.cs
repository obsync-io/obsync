using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>The History run actions are gated on a selected run (and, for diffs, a commit).</summary>
public sealed class HistoryViewModelTests
{
    private static HistoryViewModel NewViewModel() => new(
        Substitute.For<IRunRepository>(), Substitute.For<IJobRepository>(),
        Substitute.For<IRepositoryProfileRepository>(), Substitute.For<IRunReportWriter>(),
        Substitute.For<IAppSettingsRepository>());

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
}
