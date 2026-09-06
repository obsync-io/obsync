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

    /// <summary>
    /// Serializes one entry, scrubbing credential material from the rendered JSON.
    ///
    /// The bundle is emailed to strangers, so it does not get to assume its inputs are clean. The
    /// assumption above — that config models carry no secrets — holds for every field the product
    /// sets, but <c>RemoteUrl</c> is free text an operator can put anything into, and it was written
    /// out verbatim. Scrubbing the rendered text rather than named fields means a credential added to
    /// some other field later is caught too, without anyone having to remember this file exists.
    ///
    /// Applied after serialization deliberately: the replacements only ever shorten a JSON string
    /// value and introduce no structural characters, so the document stays well-formed.
    /// </summary>
    private static async Task WriteJsonEntryAsync(ZipArchive archive, string entryName, object content, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(content, Json);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream);
        await writer.WriteAsync((SecretRedactor.Scrub(json) ?? json).AsMemory(), cancellationToken).ConfigureAwait(false);
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
                // SCRUBBED, not copied verbatim. Every JSON entry in this bundle goes through
                // SecretRedactor, and these files were the one asymmetry: a support bundle is the
                // artefact that leaves the machine and gets attached to a ticket, so it is the last
                // place to rely on nothing ever having logged a secret. Serilog has no redacting
                // sink here, and a library quoting a token back into an exception message it logs
                // would land in these files untouched.
                //
                // Read line by line rather than whole-file: a rolling log can be tens of megabytes
                // and this runs on the UI thread's task.
                var entry = archive.CreateEntry($"logs/{log.Name}", CompressionLevel.Optimal);
                using var source = new StreamReader(log.OpenRead());
                using var destination = new StreamWriter(entry.Open());
                while (source.ReadLine() is { } line)
                {
                    destination.WriteLine(SecretRedactor.Scrub(line) ?? line);
                }
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
