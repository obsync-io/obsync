using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Obsync.App;
using Obsync.App.ViewModels;

namespace Obsync.App.Tests;

/// <summary>
/// Validates the desktop application's dependency-injection graph. These tests would have caught
/// the missing scheduler registration that broke the Create Sync Job flow at runtime even though
/// the solution compiled and the app launched.
/// </summary>
public sealed class AppCompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddObsyncApp(
            Path.Combine(Path.GetTempPath(), $"obsync-test-{Guid.NewGuid():N}.db"),
            Path.GetTempPath());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(typeof(MainViewModel))]
    [InlineData(typeof(DashboardViewModel))]
    [InlineData(typeof(JobsViewModel))]
    [InlineData(typeof(ServersViewModel))]
    [InlineData(typeof(ServerDialogViewModel))]
    [InlineData(typeof(RepositoriesViewModel))]
    [InlineData(typeof(RepositoryDialogViewModel))]
    [InlineData(typeof(HistoryViewModel))]
    [InlineData(typeof(SettingsViewModel))]
    [InlineData(typeof(CreateJobViewModel))]
    [InlineData(typeof(JobDetailViewModel))]
    [InlineData(typeof(DependencyExplorerViewModel))]
    [InlineData(typeof(ScriptDiffViewModel))]
    public void EveryViewModel_ResolvesFromTheContainer(Type viewModelType)
    {
        using var provider = BuildProvider();

        var resolved = provider.GetRequiredService(viewModelType);

        Assert.NotNull(resolved);
    }

    [Fact]
    public void ShellNavigator_ResolvesToTheMainViewModel()
    {
        using var provider = BuildProvider();

        Assert.IsType<MainViewModel>(provider.GetRequiredService<IShellNavigator>());
    }
}
