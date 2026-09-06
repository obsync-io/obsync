using Obsync.Service;

namespace Obsync.Service.Tests;

/// <summary>
/// The stop budget has to fit inside the deadline Windows Installer actually gives it.
/// </summary>
/// <remarks>
/// This is the test that was missing when the terminate-on-expiry behaviour was written. The budget
/// was set to 90 seconds and the process terminated at 92, on the unexamined assumption that
/// whoever asked for the stop would wait. The ServiceControl table's <c>Wait</c> column is
/// documented as "a maximum of 30 seconds", after which the installer proceeds to DeleteServices
/// and RemoveFiles regardless — so the terminate fired about a minute after the files it was
/// supposed to unlock had already been deferred to a reboot. It protected nothing, on both the
/// upgrade and the uninstall path.
/// <para>
/// Numbers are asserted against each other rather than against literals, so the relationship
/// survives someone changing one of them.
/// </para>
/// </remarks>
public sealed class ShutdownBudgetTests
{
    [Fact]
    public void TheWholeStop_FinishesBeforeWindowsInstallerStopsWaiting()
    {
        Assert.True(
            ServiceShutdownBudget.WorstCaseStop < ServiceShutdownBudget.MsiStopServicesCap,
            $"The service can take up to {ServiceShutdownBudget.WorstCaseStop.TotalSeconds}s to stop, but "
            + $"Windows Installer waits at most {ServiceShutdownBudget.MsiStopServicesCap.TotalSeconds}s "
            + "before deleting the service and removing files over a still-running process. The terminate "
            + "must land while the installer still cares.");
    }

    [Fact]
    public void ThereIsRealMarginForTheServiceControlManagersOwnLatency()
    {
        // Landing at 29.9s would satisfy the test above and still lose the race in practice: the SCM
        // takes time to dispatch the stop, and MSI's clock starts before ours does. A quarter of the
        // cap is a defensible reserve and keeps the assertion honest rather than nominal.
        var margin = ServiceShutdownBudget.MsiStopServicesCap - ServiceShutdownBudget.WorstCaseStop;

        Assert.True(
            margin >= ServiceShutdownBudget.MsiStopServicesCap / 4,
            $"Only {margin.TotalSeconds}s of margin against the installer's "
            + $"{ServiceShutdownBudget.MsiStopServicesCap.TotalSeconds}s cap.");
    }

    [Fact]
    public void TheDrain_LeavesBudgetForEverythingThatStopsAfterIt()
    {
        // RunCancellationOnStopService stops first and must not consume the whole budget: the
        // scheduling bootstrapper's shutdown database writes and Quartz's WaitForJobsToComplete
        // both still have to run inside what is left.
        Assert.True(
            ServiceShutdownBudget.Drain < ServiceShutdownBudget.Total,
            "The drain deadline must be shorter than the total shutdown budget.");

        Assert.True(
            ServiceShutdownBudget.Drain <= ServiceShutdownBudget.Total / 2,
            $"The drain takes {ServiceShutdownBudget.Drain.TotalSeconds}s of a "
            + $"{ServiceShutdownBudget.Total.TotalSeconds}s budget, leaving too little for the hosted "
            + "services that stop after it.");
    }

    [Fact]
    public void TheGrace_IsShortEnoughToBeAGraceAndLongEnoughToNotRaceACleanStop()
    {
        Assert.InRange(
            ServiceShutdownBudget.TerminateGrace,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheInstallerCap_IsTheDocumentedThirtySeconds()
    {
        // Pinned as a literal on purpose: it is not ours to choose. It comes from the ServiceControl
        // table's Wait column and is not configurable from the package. If this ever changes it
        // should be a deliberate edit with a fresh citation, not a silent drift.
        Assert.Equal(TimeSpan.FromSeconds(30), ServiceShutdownBudget.MsiStopServicesCap);
    }
}
