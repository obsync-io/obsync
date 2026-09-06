using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Obsync.Service;

namespace Obsync.Service.Tests;

/// <summary>
/// The framework's Windows service lifetime must be replaced, not merely joined.
/// </summary>
/// <remarks>
/// <see cref="ObsyncWindowsServiceLifetime"/> exists because
/// <see cref="WindowsServiceLifetime"/> reports SERVICE_STOPPED once its budget expires whether or
/// not the host actually drained, and asks the SCM for zero time while it waits. Neither correction
/// happens unless the replacement genuinely takes effect — and an extra registration alongside the
/// original would still resolve "correctly" in a smoke test, because the last registration wins.
/// This asserts there is exactly one.
///
/// <para>
/// The real call site is guarded by <c>WindowsServiceHelpers.IsWindowsService()</c>, which is false
/// under a test runner, so the framework registration is staged here the same way
/// <c>AddWindowsService</c> stages it.
/// </para>
/// </remarks>
public sealed class ServiceLifetimeRegistrationTests
{
    [Fact]
    public void TheFrameworkLifetime_IsReplacedRatherThanSupplemented()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostLifetime, WindowsServiceLifetime>();

        services.UseObsyncWindowsServiceLifetime();

        var lifetime = Assert.Single(services, d => d.ServiceType == typeof(IHostLifetime));
        Assert.Equal(typeof(ObsyncWindowsServiceLifetime), lifetime.ImplementationType);
    }

    [Fact]
    public void TheLifetime_IsASingleton()
    {
        // It is a ServiceBase the SCM holds; a second instance would mean two status handles.
        var services = new ServiceCollection();
        services.AddSingleton<IHostLifetime, WindowsServiceLifetime>();

        services.UseObsyncWindowsServiceLifetime();

        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, d => d.ServiceType == typeof(IHostLifetime)).Lifetime);
    }

    [Fact]
    public void TheLifetime_DerivesFromTheFrameworkOne_SoTheFatalExitPathStillFinds()
    {
        // Program.cs reports a fatal startup failure to the SCM via
        // `host.Services.GetService<IHostLifetime>() is ServiceBase`, which is what makes the MSI's
        // restart-on-failure recovery fire. Substituting a lifetime that is not a ServiceBase would
        // silently disable that.
        Assert.True(typeof(WindowsServiceLifetime).IsAssignableFrom(typeof(ObsyncWindowsServiceLifetime)));
        Assert.True(typeof(System.ServiceProcess.ServiceBase).IsAssignableFrom(typeof(ObsyncWindowsServiceLifetime)));
    }

    [Fact]
    public void TheLifetime_KeepsTheConstructorTheContainerNeeds()
    {
        // Resolved by DI, so a base-class constructor change must fail here rather than at service
        // start on a customer's machine, where the only symptom is "the service would not start".
        var constructor = Assert.Single(typeof(ObsyncWindowsServiceLifetime).GetConstructors());

        Assert.Equal(
            new[]
            {
                typeof(IHostEnvironment),
                typeof(IHostApplicationLifetime),
                typeof(Microsoft.Extensions.Logging.ILoggerFactory),
                typeof(Microsoft.Extensions.Options.IOptions<HostOptions>),
                typeof(Microsoft.Extensions.Options.IOptions<WindowsServiceLifetimeOptions>),
            },
            constructor.GetParameters().Select(p => p.ParameterType));
    }
}
