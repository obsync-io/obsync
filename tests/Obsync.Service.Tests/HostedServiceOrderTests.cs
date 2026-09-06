using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Obsync.Service;

namespace Obsync.Service.Tests;

/// <summary>
/// The order hosted services are registered in is the order shutdown happens in, reversed — and
/// the whole shutdown budget is shared between them.
/// </summary>
/// <remarks>
/// This shipped wrong. <see cref="RunCancellationOnStopService"/> was registered immediately after
/// the Quartz host, which satisfied the rule it was written for ("cancel before Quartz waits") but
/// broke a rule nobody had written down: a service registered LATER stops EARLIER.
/// <see cref="JobSchedulingBootstrapper"/> was registered after it and therefore stopped before it,
/// and its <c>StopAsync</c> performs two synchronous SQLite writes — each able to block for the 30s
/// busy timeout, against a lock most likely held by the very run nothing had yet asked to stop.
/// Up to 60 seconds of a 90 second budget could be spent before the first interrupt was issued.
/// <para>
/// Asserted on the descriptor list rather than on source text, so it reflects what the container
/// will actually do.
/// </para>
/// </remarks>
public sealed class HostedServiceOrderTests
{
    private static List<Type?> HostedServiceOrder()
    {
        var services = new ServiceCollection();
        services.AddObsyncHostedServices();
        return services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();
    }

    [Fact]
    public void CancellingInFlightRuns_IsRegisteredLast_SoItStopsFirst()
    {
        Assert.Equal(typeof(RunCancellationOnStopService), HostedServiceOrder()[^1]);
    }

    [Fact]
    public void CancellingInFlightRuns_StopsBeforeTheBookkeepingThatCanBlockOnTheSameRow()
    {
        var order = HostedServiceOrder();

        // Registration order is start order; stop order is its reverse. "Stops before" therefore
        // means "registered after".
        Assert.True(
            order.IndexOf(typeof(RunCancellationOnStopService)) > order.IndexOf(typeof(JobSchedulingBootstrapper)),
            "RunCancellationOnStopService must be registered after JobSchedulingBootstrapper so that it "
            + "STOPS first — otherwise the bootstrapper's shutdown database writes can consume the "
            + "shutdown budget before any in-flight run has been asked to cancel. "
            + $"Actual order: {string.Join(" -> ", order.Select(t => t?.Name))}");
    }

    [Fact]
    public void QuartzsWaitForJobsToComplete_IsRegisteredFirst_SoItStopsLast()
    {
        // Quartz only waits; it never signals cancellation. Waiting first would burn the budget
        // watching a run that had not been interrupted.
        //
        // Matched on the owning assembly rather than a type name: AddQuartzHostedService registers
        // NamedSchedulerHostedService in Quartz 3.18, and pinning the concrete name would turn a
        // Quartz upgrade into a spurious failure of a test that is about ORDER.
        var order = HostedServiceOrder();
        var first = order[0];

        Assert.NotNull(first);
        Assert.StartsWith("Quartz", first!.Assembly.GetName().Name!, StringComparison.Ordinal);
        Assert.All(
            order.Skip(1),
            t => Assert.Equal("Obsync.Service", t?.Assembly.GetName().Name));
    }

    [Fact]
    public void EveryHostedService_IsRegisteredExactlyOnce()
    {
        var order = HostedServiceOrder();
        Assert.Equal(order.Count, order.Distinct().Count());
    }
}
