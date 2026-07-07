using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>Exercises the V006 app_settings table: the proxy and alert configurations round-trip
/// as JSON, and the update-check bookkeeping keys persist.</summary>
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

    [Fact]
    public async Task Alerts_DefaultsWhenUnset()
    {
        var settings = await _provider.GetRequiredService<IAppSettingsRepository>().GetAlertSettingsAsync();

        Assert.False(settings.EmailEnabled);
        Assert.False(settings.WebhookEnabled);
        Assert.Equal(587, settings.SmtpPort);
        Assert.True(settings.SmtpUseTls);
        Assert.True(settings.OnFailure);
        Assert.True(settings.OnWarning);
        Assert.False(settings.OnChanges);
        Assert.True(settings.ScheduledRunsOnly);
    }

    [Fact]
    public async Task Alerts_RoundTrip()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();
        await repo.UpsertAlertSettingsAsync(new AlertSettings
        {
            EmailEnabled = true,
            SmtpHost = "smtp.corp.example",
            SmtpPort = 25,
            SmtpUseTls = false,
            SmtpUsername = "svc-obsync",
            FromAddress = "obsync@corp.example",
            ToAddresses = "dba@corp.example, ops@corp.example",
            WebhookEnabled = true,
            WebhookUrl = "https://hooks.corp.example/obsync",
            OnFailure = false,
            OnWarning = false,
            OnChanges = true,
            ScheduledRunsOnly = false,
        });

        var loaded = await repo.GetAlertSettingsAsync();

        Assert.True(loaded.EmailEnabled);
        Assert.Equal("smtp.corp.example", loaded.SmtpHost);
        Assert.Equal(25, loaded.SmtpPort);
        Assert.False(loaded.SmtpUseTls);
        Assert.Equal("svc-obsync", loaded.SmtpUsername);
        Assert.Equal("obsync@corp.example", loaded.FromAddress);
        Assert.Equal("dba@corp.example, ops@corp.example", loaded.ToAddresses);
        Assert.True(loaded.WebhookEnabled);
        Assert.Equal("https://hooks.corp.example/obsync", loaded.WebhookUrl);
        Assert.False(loaded.OnFailure);
        Assert.False(loaded.OnWarning);
        Assert.True(loaded.OnChanges);
        Assert.False(loaded.ScheduledRunsOnly);
    }

    [Fact]
    public async Task LastUpdateCheck_DefaultsToNull_WhenUnset()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();
        Assert.Null(await repo.GetLastUpdateCheckAsync());
    }

    [Fact]
    public async Task LastUpdateCheck_RoundTrips()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();
        var timestamp = new DateTimeOffset(2026, 7, 5, 8, 30, 15, TimeSpan.Zero);

        await repo.SetLastUpdateCheckAsync(timestamp);

        Assert.Equal(timestamp, await repo.GetLastUpdateCheckAsync());
    }

    [Fact]
    public async Task LastNotifiedUpdateVersion_DefaultsToNull_WhenUnset()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();
        Assert.Null(await repo.GetLastNotifiedUpdateVersionAsync());
    }

    [Fact]
    public async Task LastNotifiedUpdateVersion_RoundTrips_AndOverwrites()
    {
        var repo = _provider.GetRequiredService<IAppSettingsRepository>();

        await repo.SetLastNotifiedUpdateVersionAsync("0.5.0");
        Assert.Equal("0.5.0", await repo.GetLastNotifiedUpdateVersionAsync());

        await repo.SetLastNotifiedUpdateVersionAsync("0.6.0");
        Assert.Equal("0.6.0", await repo.GetLastNotifiedUpdateVersionAsync());
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
