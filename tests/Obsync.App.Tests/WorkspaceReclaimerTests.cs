using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Reclaiming repository clones. A workspace lives at
/// <c>&lt;workspacesRoot&gt;/&lt;repositoryProfileId:N&gt;</c>, so the profile's GUID is the only thing
/// that can name it — and nothing ever deleted one. Deleting a repository left the clone, possibly
/// many gigabytes of a schema estate, with no reference anywhere in the product.
/// </summary>
public sealed class WorkspaceReclaimerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obsync-reclaim-{Guid.NewGuid():N}");
    private readonly IAppSettingsRepository _settings = Substitute.For<IAppSettingsRepository>();
    private readonly IRepositoryProfileRepository _repositories = Substitute.For<IRepositoryProfileRepository>();

    public WorkspaceReclaimerTests()
    {
        Directory.CreateDirectory(_root);
        _settings.GetWorkspacesRootOverrideAsync(Arg.Any<CancellationToken>()).Returns(_root);
        Live();
    }

    private void Live(params GitRepositoryProfile[] profiles) =>
        _repositories.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitRepositoryProfile>>(profiles));

    private WorkspaceReclaimer Build() =>
        new(_settings, _repositories, NullLogger<WorkspaceReclaimer>.Instance);

    private string MakeClone(Guid id, int bytes = 64)
    {
        var path = Path.Combine(_root, id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllBytes(Path.Combine(path, "schema.sql"), new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReclaimForRepository_DeletesThatClone_AndReportsWhatItFreed()
    {
        var id = Guid.NewGuid();
        var path = MakeClone(id, bytes: 128);

        var freed = await Build().ReclaimForRepositoryAsync(id);

        Assert.False(Directory.Exists(path));
        Assert.True(freed >= 128, $"expected at least the file's bytes; got {freed}.");
    }

    [Fact]
    public async Task ReclaimForRepository_IsSilentWhenThereIsNoClone()
    {
        // A repository deleted before it ever ran has no workspace; that is not an error.
        Assert.Equal(0, await Build().ReclaimForRepositoryAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReclaimForRepository_RemovesReadOnlyGitObjects()
    {
        // git marks pack files and loose objects read-only, and Directory.Delete refuses those —
        // so without clearing the attribute the reclaim would silently fail on every real clone.
        var id = Guid.NewGuid();
        var path = MakeClone(id);
        var packed = Path.Combine(path, ".git", "packed-refs");
        File.WriteAllText(packed, "ref");
        File.SetAttributes(packed, FileAttributes.ReadOnly);

        await Build().ReclaimForRepositoryAsync(id);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task FindOrphans_IgnoresClonesThatStillHaveAProfile()
    {
        var live = Guid.NewGuid();
        MakeClone(live);
        Live(new GitRepositoryProfile { Id = live, Name = "R", Owner = "o", RepositoryName = "r" });

        Assert.Empty(await Build().FindOrphansAsync());
    }

    [Fact]
    public async Task FindOrphans_ReportsACloneWhoseProfileIsGone()
    {
        var orphan = Guid.NewGuid();
        var path = MakeClone(orphan, bytes: 256);

        var found = Assert.Single(await Build().FindOrphansAsync());

        Assert.Equal(path, found.Path);
        Assert.True(found.Bytes >= 256);
    }

    [Fact]
    public async Task FindOrphans_NeverTouchesDirectoriesThatAreNotWorkspaces()
    {
        // The workspaces root is user-settable in Settings, so it can legitimately be a folder that
        // holds other things. Deleting those because they are not in the profile list would be
        // catastrophic and entirely our fault — only GUID-named directories are ever candidates.
        Directory.CreateDirectory(Path.Combine(_root, "my-important-folder"));
        Directory.CreateDirectory(Path.Combine(_root, "backups"));

        Assert.Empty(await Build().FindOrphansAsync());
    }

    [Fact]
    public async Task ReclaimOrphans_RemovesOnlyTheOrphans()
    {
        var live = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var livePath = MakeClone(live);
        var orphanPath = MakeClone(orphan);
        var keep = Path.Combine(_root, "not-a-workspace");
        Directory.CreateDirectory(keep);
        Live(new GitRepositoryProfile { Id = live, Name = "R", Owner = "o", RepositoryName = "r" });

        var (removed, bytes) = await Build().ReclaimOrphansAsync();

        Assert.Equal(1, removed);
        Assert.True(bytes > 0);
        Assert.False(Directory.Exists(orphanPath));
        Assert.True(Directory.Exists(livePath));
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public async Task WithNoOverride_TheBuiltInRootIsUsed()
    {
        // Must agree with SyncEngine.ResolveWorkspacesRootAsync, or this would clean a location no
        // run ever writes to while the real clones accumulate untouched.
        _settings.GetWorkspacesRootOverrideAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        // No assertion on contents — the point is that it resolves and does not throw against the
        // real default root, whatever this machine's is.
        Assert.NotNull(await Build().FindOrphansAsync());
        Assert.NotNull(ObsyncPaths.WorkspacesRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
