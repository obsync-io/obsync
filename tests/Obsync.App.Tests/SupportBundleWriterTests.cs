using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Obsync.App.Services;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The support bundle must contain the expected artifacts and never leak secrets. Runs against a real
/// SQLite database seeded with a SQL-login server and a repo, then reopens the zip to assert contents.
/// </summary>
public sealed class SupportBundleWriterTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-bundle-db-{Guid.NewGuid():N}.db");
    private readonly string _zipPath = Path.Combine(Path.GetTempPath(), $"obsync-bundle-{Guid.NewGuid():N}.zip");
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

    [Fact]
    public async Task WriteAsync_ProducesExpectedEntries_AndLeaksNoSecrets()
    {
        var server = new SqlConnectionProfile
        {
            Name = "PROD-SQL01",
            ServerName = "PROD-SQL01",
            AuthenticationMode = SqlAuthenticationMode.SqlLogin,
            Username = "svc_reader",
        };
        var repo = new GitRepositoryProfile { Name = "history", Owner = "acme", RepositoryName = "sql-history" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(server);
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);
        var job = new SyncJob
        {
            Name = "Prod Sync",
            ConnectionProfileId = server.Id,
            RepositoryProfileId = repo.Id,
            Databases = ["SalesDB"],
        };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        var writer = new SupportBundleWriter(
            _provider.GetRequiredService<IJobRepository>(),
            _provider.GetRequiredService<IConnectionProfileRepository>(),
            _provider.GetRequiredService<IRepositoryProfileRepository>(),
            _provider.GetRequiredService<IRunRepository>(),
            SystemClock.Instance,
            _provider.GetRequiredService<IAppSettingsRepository>());

        await writer.WriteAsync(_zipPath, [new DiagnosticResult("Git CLI", DiagnosticStatus.Pass, "git version 2.45.0", DateTimeOffset.UnixEpoch)]);

        using var zip = ZipFile.OpenRead(_zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("system-info.json", names);
        Assert.Contains("diagnostics.json", names);
        Assert.Contains("config.json", names);
        Assert.Contains("recent-runs.json", names);

        var config = ReadEntry(zip, "config.json");
        Assert.Contains("PROD-SQL01", config);  // server present
        Assert.Contains("acme", config);         // repo owner present
        Assert.Contains("svc_reader", config);   // username is not a secret and is expected
        // No secret field is serialized — the models never hold passwords/tokens (they live in
        // Windows Credential Manager). Guard against a field literally named Password/Token.
        // (The computed "RequiresPassword" flag is fine — hence the exact quoted-name check.)
        Assert.DoesNotContain("\"Password\"", config, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Token\"", config, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Secret\"", config, StringComparison.OrdinalIgnoreCase);

        var systemInfo = ReadEntry(zip, "system-info.json");
        Assert.Contains("AppVersion", systemInfo);
        Assert.Contains("git version 2.45.0", systemInfo); // GitVersion pulled from the diagnostics
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _provider?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _zipPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
