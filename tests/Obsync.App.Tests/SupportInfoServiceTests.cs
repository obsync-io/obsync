using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Obsync.App.Services;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// The About tab's support information: the schema version comes from the real migrations table,
/// the service version only from a FRESH heartbeat, and — the redaction guarantee — the row keys are
/// a fixed whitelist of version/environment facts, so the copyable block can never carry a secret.
/// </summary>
public sealed class SupportInfoServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-support-db-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddObsyncData(_dbPath);
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private SupportInfoService NewService(IClock? clock = null)
    {
        var diagnostics = Substitute.For<IDiagnosticsService>();
        diagnostics.GetGitVersionAsync(Arg.Any<CancellationToken>()).Returns("git version 2.45.0 — from PATH");
        return new SupportInfoService(
            _provider.GetRequiredService<IAppSettingsRepository>(),
            _provider.GetRequiredService<IDbConnectionFactory>(),
            diagnostics,
            clock ?? SystemClock.Instance);
    }

    [Fact]
    public async Task GetAsync_ReadsTheHighestAppliedMigration_AsTheSchemaVersion()
    {
        var rows = await NewService().GetAsync();

        var schema = rows.Single(r => r.Key == "State database schema").Value;
        Assert.Matches(@"^V\d+$", schema);

        // It is exactly the (prefix of the) MAX version in the table the initializer maintains.
        await using var connection = await _provider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM __migrations;";
        var expected = (string)(await command.ExecuteScalarAsync())!;
        Assert.StartsWith(schema, expected);
    }

    [Fact]
    public async Task GetAsync_ReportsNotRunning_WithoutAFreshHeartbeat()
    {
        var rows = await NewService().GetAsync();

        Assert.Equal("not running", rows.Single(r => r.Key == "Windows Service").Value);
    }

    [Fact]
    public async Task GetAsync_ReportsTheServiceVersion_FromAFreshHeartbeat()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        await _provider.GetRequiredService<IAppSettingsRepository>().SetSchedulerHeartbeatAsync(
            new SchedulerHeartbeat { TimestampUtc = now.AddSeconds(-10), Account = "ACME\\svc", Version = "0.9.0" });

        var rows = await NewService(clock).GetAsync();

        Assert.Equal("0.9.0", rows.Single(r => r.Key == "Windows Service").Value);
    }

    [Fact]
    public async Task GetAsync_RowKeys_AreTheFixedWhitelist()
    {
        // Redaction by construction: every row is a version, an OS fact, or a schema id. A new key
        // must be reviewed against this whitelist before it can ship (and can never be a secret).
        string[] whitelist =
            ["Obsync", "Engine", "Windows Service", ".NET runtime", "Git", "Windows", "State database schema"];

        var rows = await NewService().GetAsync();

        Assert.Equal(whitelist, rows.Select(r => r.Key));
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
