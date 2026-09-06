using System.IO;
using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
using Obsync.Shared;

namespace Obsync.App.Services;

/// <summary>A clone directory with no repository profile left to name it.</summary>
/// <param name="Path">Full path to the directory.</param>
/// <param name="Bytes">Size on disk, best-effort (unreadable files are skipped, not fatal).</param>
public sealed record OrphanedWorkspace(string Path, long Bytes);

/// <summary>
/// Finds and deletes repository clones that nothing references any more.
/// </summary>
/// <remarks>
/// A workspace lives at <c>&lt;workspacesRoot&gt;/&lt;repositoryProfileId:N&gt;</c>, so the profile's
/// GUID is the ONLY thing that can name it. Deleting the profile removed the row and the credential
/// and left the clone — potentially many gigabytes of a schema estate — with no remaining reference
/// in the product, no surface listing it, and nothing that could ever reclaim it. Relocating the
/// workspaces root in Settings leaks the whole previous tree the same way, and a clone killed by the
/// command timeout leaves a partial one that is only ever reused if that same profile runs again.
/// <para>
/// Deletion is always safe here: everything in a workspace is regenerable, from the remote and from
/// SQL Server. That is the same justification <c>CloneFreshAsync</c> already relies on.
/// </para>
/// </remarks>
public interface IWorkspaceReclaimer
{
    /// <summary>Deletes one repository's clone. Returns the bytes freed, or 0 when there was none.</summary>
    Task<long> ReclaimForRepositoryAsync(Guid repositoryProfileId, CancellationToken cancellationToken = default);

    /// <summary>Clone directories under the effective root that no repository profile claims.</summary>
    Task<IReadOnlyList<OrphanedWorkspace>> FindOrphansAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every orphan. Returns how many were removed and the bytes freed.</summary>
    Task<(int Removed, long Bytes)> ReclaimOrphansAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkspaceReclaimer" />
public sealed class WorkspaceReclaimer : IWorkspaceReclaimer
{
    private readonly IAppSettingsRepository _settings;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly ILogger<WorkspaceReclaimer> _logger;

    public WorkspaceReclaimer(
        IAppSettingsRepository settings, IRepositoryProfileRepository repositories, ILogger<WorkspaceReclaimer> logger)
    {
        _settings = settings;
        _repositories = repositories;
        _logger = logger;
    }

    /// <summary>
    /// The workspaces root in force: the Settings override when set, else the built-in default.
    /// Must match <c>SyncEngine.ResolveWorkspacesRootAsync</c>, or this would clean a location no
    /// run ever writes to and leave the real clones untouched.
    /// </summary>
    private async Task<string> RootAsync(CancellationToken cancellationToken)
    {
        var over = await _settings.GetWorkspacesRootOverrideAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(over) ? ObsyncPaths.WorkspacesRoot : over.Trim();
    }

    public async Task<long> ReclaimForRepositoryAsync(Guid repositoryProfileId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(await RootAsync(cancellationToken).ConfigureAwait(false), repositoryProfileId.ToString("N"));
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var bytes = MeasureDirectory(path);
        return Delete(path) ? bytes : 0;
    }

    public async Task<IReadOnlyList<OrphanedWorkspace>> FindOrphansAsync(CancellationToken cancellationToken = default)
    {
        var root = await RootAsync(cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var live = (await _repositories.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => r.Id.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = new List<OrphanedWorkspace>();
        foreach (var directory in SafeEnumerate(root))
        {
            var name = Path.GetFileName(directory);

            // Only directories that LOOK like a workspace are candidates. The root is user-settable
            // in Settings, so it can legitimately be a folder holding other things — deleting those
            // because they are not in the profile list would be catastrophic and entirely our fault.
            if (!Guid.TryParseExact(name, "N", out _) || live.Contains(name))
            {
                continue;
            }

            orphans.Add(new OrphanedWorkspace(directory, MeasureDirectory(directory)));
        }

        return orphans;
    }

    public async Task<(int Removed, long Bytes)> ReclaimOrphansAsync(CancellationToken cancellationToken = default)
    {
        var removed = 0;
        long bytes = 0;
        foreach (var orphan in await FindOrphansAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Delete(orphan.Path))
            {
                removed++;
                bytes += orphan.Bytes;
            }
        }

        return (removed, bytes);
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Size on disk, best-effort. A clone is being reported on, not audited, so an unreadable file
    /// contributes nothing rather than failing the whole measurement.
    /// </summary>
    private static long MeasureDirectory(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip this file.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partial total is still worth reporting.
        }

        return total;
    }

    private bool Delete(string path)
    {
        try
        {
            // git marks pack files and objects read-only; Directory.Delete refuses those.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Delete will report the real problem.
                }
            }

            Directory.Delete(path, recursive: true);
            _logger.LogInformation("Reclaimed the workspace at {Path}.", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never fatal: reclaiming disk is housekeeping, and a clone held open by an antivirus
            // scan or an Explorer window must not fail the delete the user actually asked for.
            _logger.LogWarning(ex, "Could not reclaim the workspace at {Path}.", path);
            return false;
        }
    }
}
