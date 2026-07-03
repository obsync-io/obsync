using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>Exercises the V006 app_settings table: the proxy configuration round-trips as JSON.</summary>
public sealed class AppSettingsPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-settings-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(_dbPath);
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Proxy_DefaultsToNone_WhenUnset()
    {
        var settings = await _provider.GetRequiredService<IAppSettingsRepository>().GetProxyAsync();
        Assert.Equal(ProxyMode.None, settings.Mode);
    }

    [Fact]
    public async Task Proxy_RoundTrips()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();
        await repo.UpsertProxyAsync(new ProxySettings
        {
            Mode = ProxyMode.Manual,
            Url = "http://proxy.corp:8080",
            Username = "svc",
            BypassHosts = ["github.internal", "10.0.0.0/8"],
        });

        var loaded = await repo.GetProxyAsync();

        Assert.Equal(ProxyMode.Manual, loaded.Mode);
        Assert.Equal("http://proxy.corp:8080", loaded.Url);
        Assert.Equal("svc", loaded.Username);
        Assert.Equal(["github.internal", "10.0.0.0/8"], loaded.BypassHosts);
    }

    [Fact]
    public async Task Proxy_Upsert_Overwrites()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();
        await repo.UpsertProxyAsync(new ProxySettings { Mode = ProxyMode.Manual, Url = "http://a:1" });
        await repo.UpsertProxyAsync(new ProxySettings { Mode = ProxyMode.System });

        var loaded = await repo.GetProxyAsync();

        Assert.Equal(ProxyMode.System, loaded.Mode);
        Assert.Null(loaded.Url);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
