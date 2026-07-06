using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;

namespace Obsync.App.Services;

/// <summary>Writes a secret-free support bundle (.zip) for troubleshooting.</summary>
public interface ISupportBundleWriter
{
    Task WriteAsync(string zipPath, IReadOnlyList<DiagnosticResult> diagnostics, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISupportBundleWriter" />
public sealed class SupportBundleWriter : ISupportBundleWriter
{
    private const int MaxLogFiles = 5;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _servers;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IRunRepository _runs;
    private readonly IClock _clock;
    private readonly IAppSettingsRepository _settings;

    public SupportBundleWriter(
        IJobRepository jobs,
        IConnectionProfileRepository servers,
        IRepositoryProfileRepository repositories,
        IRunRepository runs,
        IClock clock,
        IAppSettingsRepository settings)
    {
        _jobs = jobs;
        _servers = servers;
        _repositories = repositories;
        _runs = runs;
        _clock = clock;
        _settings = settings;
    }

    public async Task WriteAsync(string zipPath, IReadOnlyList<DiagnosticResult> diagnostics, CancellationToken cancellationToken = default)
    {
        // Config models never carry secrets (passwords/tokens live in Windows Credential Manager),
        // so these dumps are safe to include as-is.
        var jobs = await _jobs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var servers = await _servers.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var repositories = await _repositories.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var runs = await _runs.GetRecentAsync(50, cancellationToken).ConfigureAwait(false);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        // The bundle exists for accurate troubleshooting — report the EFFECTIVE workspaces root
        // (the Settings override when set), not the built-in default.
        var workspacesOverride = await _settings.GetWorkspacesRootOverrideAsync(cancellationToken).ConfigureAwait(false);
        var workspacesRoot = string.IsNullOrWhiteSpace(workspacesOverride) ? ObsyncPaths.WorkspacesRoot : workspacesOverride;

        await using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        await WriteJsonEntryAsync(archive, "system-info.json", BuildSystemInfo(diagnostics, workspacesRoot), cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(archive, "diagnostics.json", diagnostics, cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(archive, "config.json", new { jobs, servers, repositories }, cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(archive, "recent-runs.json", runs, cancellationToken).ConfigureAwait(false);

        AddRecentLogs(archive);
    }

    private object BuildSystemInfo(IReadOnlyList<DiagnosticResult> diagnostics, string workspacesRoot) => new
    {
        GeneratedAtUtc = _clock.UtcNow,
        AppVersion = VersionInfo.Of(typeof(App).Assembly),
        EngineVersion = VersionInfo.Of(typeof(Engine.ISyncEngine).Assembly),
        SqlClientVersion = VersionInfo.Of(typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly),
        GitVersion = diagnostics.FirstOrDefault(d => d.Name == "Git CLI")?.Detail ?? "unknown",
        Os = RuntimeInformation.OSDescription,
        Runtime = RuntimeInformation.FrameworkDescription,
        Architecture = RuntimeInformation.OSArchitecture.ToString(),
        MachineName = Environment.MachineName,
        User = CurrentActor.Name,
        DataRoot = ObsyncPaths.Root,
        WorkspacesRoot = workspacesRoot,
        LogsRoot = ObsyncPaths.LogsRoot,
        FreeDiskBytes = SafeFreeDisk(),
    };

    private static async Task WriteJsonEntryAsync(ZipArchive archive, string entryName, object content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, content, Json, cancellationToken).ConfigureAwait(false);
    }

    // Copy only the app/service log globs (never the state database), newest first, capped.
    private static void AddRecentLogs(ZipArchive archive)
    {
        if (!Directory.Exists(ObsyncPaths.LogsRoot))
        {
            return;
        }

        var logs = new DirectoryInfo(ObsyncPaths.LogsRoot)
            .EnumerateFiles("*.log")
            .Where(f => f.Name.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
                     || f.Name.StartsWith("service-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxLogFiles);

        foreach (var log in logs)
        {
            try
            {
                archive.CreateEntryFromFile(log.FullName, $"logs/{log.Name}", CompressionLevel.Optimal);
            }
            catch (IOException)
            {
                // A log currently being written may be locked; skip it rather than fail the bundle.
            }
        }
    }

    private static long? SafeFreeDisk()
    {
        try
        {
            var root = Path.GetPathRoot(ObsyncPaths.Root);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }
}
