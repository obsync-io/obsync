using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>The History "Export report" action is gated on a selected run.</summary>
public sealed class HistoryViewModelTests
{
    [Fact]
    public void ExportReport_IsDisabled_UntilARunIsSelected()
    {
        var vm = new HistoryViewModel(
            Substitute.For<IRunRepository>(), Substitute.For<IRunReportWriter>(), Substitute.For<IAppSettingsRepository>());

        Assert.False(vm.ExportReportCommand.CanExecute(null));

        vm.SelectedRun = new SyncRun { JobName = "Prod Sync", RunKey = "20260702-093000" };
        Assert.True(vm.ExportReportCommand.CanExecute(null));

        vm.SelectedRun = null;
        Assert.False(vm.ExportReportCommand.CanExecute(null));
    }
}
