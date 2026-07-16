using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Obsync.Git.Tests;

/// <summary>
/// Exercises <see cref="GitWorkspace"/> end to end against a throwaway local bare repository that
/// stands in for the GitHub remote — no network or GitHub account required. The git CLI is a hard
/// runtime dependency of Obsync, so these tests run wherever git is on PATH.
/// </summary>
public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly GitCommandRunner _runner = new(NullLogger<GitCommandRunner>.Instance);
    private readonly GitWorkspace _workspace;

    public GitWorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "obsync-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new GitWorkspace(_runner, NullLogger<GitWorkspace>.Instance);
    }

    [Fact]
    public async Task PrepareCommitPush_PublishesChangesToRemote()
    {
        // Obsync requires the git CLI at runtime; skip gracefully where it is unavailable (e.g. a
        // build agent without git) rather than fail. It is present in normal dev/CI environments.
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);

        var prepared = await _workspace.PrepareAsync(context);
        Assert.True(prepared.IsSuccess, prepared.Error);

        await File.WriteAllTextAsync(
            CreateFile(workPath, "schemas", "test.sql"), "CREATE SCHEMA [app];");

        var commit = await _workspace.CommitAllAsync(context, "Add app schema", "body");
        Assert.True(commit.Success, commit.Error);
        Assert.True(commit.HadChanges);
        Assert.NotNull(commit.CommitSha);
        Assert.Equal(40, commit.CommitSha!.Length);

        var push = await _workspace.PushAsync(context);
        Assert.True(push.IsSuccess, push.Error);

        // A fresh clone of the remote must contain the pushed file, proving the push landed.
        var verifyPath = Path.Combine(_root, "verify");
        var clone = await _runner.RunAsync(_root, ["clone", remote, verifyPath]);
        Assert.True(clone.Success, clone.StandardError);
        Assert.True(File.Exists(Path.Combine(verifyPath, "schemas", "test.sql")));
    }

    [Fact]
    public async Task CommitAll_WithNoChanges_ReportsNoChanges()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var context = NewContext(remote, Path.Combine(_root, "work"));

        var prepared = await _workspace.PrepareAsync(context);
        Assert.True(prepared.IsSuccess, prepared.Error);

        var commit = await _workspace.CommitAllAsync(context, "nothing", "body");

        Assert.True(commit.Success);
        Assert.False(commit.HadChanges);
        Assert.Null(commit.CommitSha);
    }

    [Fact]
    public async Task Prepare_PreservesUnpushedCommit_AfterAFailedPush()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);

        // First run: publish a file successfully.
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "first.sql"), "CREATE SCHEMA [a];");
        Assert.True((await _workspace.CommitAllAsync(context, "first", "body")).Success);
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);

        // Second run: commit locally but DO NOT push (simulates a push that failed, e.g. bad token).
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "second.sql"), "CREATE SCHEMA [b];");
        Assert.True((await _workspace.CommitAllAsync(context, "second", "body")).Success);
        Assert.True(await _workspace.HasUnpushedCommitsAsync(context));

        // Third run: PrepareAsync must NOT discard the un-pushed commit (the old bug hard-reset to origin).
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);
        Assert.True(File.Exists(Path.Combine(workPath, "schemas", "second.sql")));
        Assert.True(await _workspace.HasUnpushedCommitsAsync(context));

        // And it can still be pushed, so no work is lost.
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);
        Assert.False(await _workspace.HasUnpushedCommitsAsync(context));

        var verifyPath = Path.Combine(_root, "verify");
        Assert.True((await _runner.RunAsync(_root, ["clone", remote, verifyPath])).Success);
        Assert.True(File.Exists(Path.Combine(verifyPath, "schemas", "second.sql")));
    }

    [Fact]
    public async Task CommitAll_AllowEmpty_CreatesHeartbeatCommit()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var context = NewContext(remote, Path.Combine(_root, "work"));
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);

        // No working-tree changes, but allowEmpty requests a commit anyway (audit heartbeat).
        var commit = await _workspace.CommitAllAsync(context, "heartbeat", "no changes", allowEmpty: true);

        Assert.True(commit.Success, commit.Error);
        Assert.NotNull(commit.CommitSha);
        Assert.True(await _workspace.HasUnpushedCommitsAsync(context));
    }

    [Fact]
    public async Task HasUnpushedCommits_ReflectsPushState()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);

        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "x.sql"), "CREATE SCHEMA [x];");
        Assert.True((await _workspace.CommitAllAsync(context, "x", "body")).Success);
        Assert.True(await _workspace.HasUnpushedCommitsAsync(context));

        Assert.True((await _workspace.PushAsync(context)).IsSuccess);
        Assert.False(await _workspace.HasUnpushedCommitsAsync(context));
    }

    [Fact]
    public async Task CommitAll_ExcludesAtomicWriteTempFiles()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);

        // A hard process kill mid-write can strand an .obsync-tmp next to real output; commits
        // must never sweep it in.
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "real.sql"), "CREATE SCHEMA [r];");
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "torn.sql.obsync-tmp"), "CREATE SCH");

        var commit = await _workspace.CommitAllAsync(context, "real only", "body");
        Assert.True(commit.Success, commit.Error);
        Assert.True(commit.HadChanges);
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);

        var verifyPath = Path.Combine(_root, "verify");
        Assert.True((await _runner.RunAsync(_root, ["clone", remote, verifyPath])).Success);
        Assert.True(File.Exists(Path.Combine(verifyPath, "schemas", "real.sql")));
        Assert.False(File.Exists(Path.Combine(verifyPath, "schemas", "torn.sql.obsync-tmp")));

        // A stray temp file alone must not read as "changes" (it would create junk commits forever).
        var again = await _workspace.CommitAllAsync(context, "should be nothing", "body");
        Assert.True(again.Success, again.Error);
        Assert.False(again.HadChanges);
    }

    [Fact]
    public async Task Prepare_FreshClone_WritesLargeRepoConfigAndTmpExclude()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");

        Assert.True((await _workspace.PrepareAsync(NewContext(remote, workPath))).IsSuccess);

        // `clone -c` must persist each setting into the new clone's own config, so it also holds
        // for every later command (and core.longpaths already protected the clone's checkout).
        Assert.Equal("true", await ConfigValueAsync(workPath, "core.longpaths"));
        Assert.Equal("true", await ConfigValueAsync(workPath, "feature.manyFiles"));
        Assert.Equal("batch", await ConfigValueAsync(workPath, "core.fsyncMethod"));
        Assert.Contains("*.obsync-tmp", await File.ReadAllLinesAsync(Path.Combine(workPath, ".git", "info", "exclude")));
    }

    [Fact]
    public async Task Prepare_ExistingClone_AddsLargeRepoConfigAndTmpExclude()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");

        // A clone deployed before these repository defaults existed: plain clone, no config keys,
        // no info/exclude entry.
        Assert.True((await _runner.RunAsync(_root, ["clone", remote, workPath])).Success);
        var context = NewContext(remote, workPath);

        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);

        Assert.Equal("true", await ConfigValueAsync(workPath, "core.longpaths"));
        Assert.Equal("true", await ConfigValueAsync(workPath, "feature.manyFiles"));
        Assert.Equal("batch", await ConfigValueAsync(workPath, "core.fsyncMethod"));
        var excludePath = Path.Combine(workPath, ".git", "info", "exclude");
        Assert.Contains("*.obsync-tmp", await File.ReadAllLinesAsync(excludePath));

        // Runs on every prepare, so it must be idempotent — no duplicate exclude lines.
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);
        Assert.Single(await File.ReadAllLinesAsync(excludePath), line => line == "*.obsync-tmp");
    }

    [Fact]
    public async Task DiffCachedQuiet_ExitCodes_HoldOnAnUnbornHead()
    {
        if (!GitAvailable())
        {
            return;
        }

        // CommitAllAsync reads `diff --cached --quiet` exit codes as 0 = nothing staged and
        // 1 = staged changes; lock that contract on the unborn HEAD of a first run against an
        // empty remote, where there is no HEAD commit to diff against.
        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        Assert.True((await _workspace.PrepareAsync(NewContext(remote, workPath))).IsSuccess);

        Assert.Equal(0, (await _runner.RunAsync(workPath, ["diff", "--cached", "--quiet"])).ExitCode);

        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "x.sql"), "CREATE SCHEMA [x];");
        Assert.True((await _runner.RunAsync(workPath, ["add", "-A", "--", "."])).Success);
        Assert.Equal(1, (await _runner.RunAsync(workPath, ["diff", "--cached", "--quiet"])).ExitCode);
    }

    [Fact]
    public async Task Prepare_RecoversFromAPartialClone()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");

        // A cancelled/killed first clone leaves a .git-less remnant that used to fail every later
        // clone with "destination path already exists and is not an empty directory".
        Directory.CreateDirectory(Path.Combine(workPath, "schemas"));
        await File.WriteAllTextAsync(Path.Combine(workPath, "schemas", "partial.sql"), "CREATE SCH");

        var prepared = await _workspace.PrepareAsync(NewContext(remote, workPath));

        Assert.True(prepared.IsSuccess, prepared.Error);
        Assert.True(Directory.Exists(Path.Combine(workPath, ".git")));
        Assert.False(File.Exists(Path.Combine(workPath, "schemas", "partial.sql"))); // remnant cleared
    }

    [Fact]
    public async Task Prepare_RecoversFromACorruptGitDirectory()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");

        // .git exists but is empty (interrupted mid-clone): fetch fails, the workspace is
        // detected as corrupt, deleted, and recloned instead of failing every future run.
        Directory.CreateDirectory(Path.Combine(workPath, ".git"));

        var prepared = await _workspace.PrepareAsync(NewContext(remote, workPath));

        Assert.True(prepared.IsSuccess, prepared.Error);
        Assert.True(File.Exists(Path.Combine(workPath, ".git", "HEAD"))); // a real clone now
    }

    [Fact]
    public async Task Prepare_RemovesAStaleIndexLock()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "first.sql"), "CREATE SCHEMA [a];");
        Assert.True((await _workspace.CommitAllAsync(context, "first", "body")).Success);
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);

        // A kill during add/commit strands .git/index.lock; fetch and rev-parse still succeed, so
        // without explicit removal every later run fails with "index.lock: File exists".
        await File.WriteAllTextAsync(Path.Combine(workPath, ".git", "index.lock"), "");

        var prepared = await _workspace.PrepareAsync(context);
        Assert.True(prepared.IsSuccess, prepared.Error);
        Assert.False(File.Exists(Path.Combine(workPath, ".git", "index.lock")));

        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "second.sql"), "CREATE SCHEMA [b];");
        var commit = await _workspace.CommitAllAsync(context, "second", "body");
        Assert.True(commit.Success, commit.Error);
    }

    [Fact]
    public async Task Prepare_PointsOriginAtTheProfileRemoteUrl()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remoteA = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var contextA = NewContext(remoteA, workPath);
        Assert.True((await _workspace.PrepareAsync(contextA)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "a.sql"), "CREATE SCHEMA [a];");
        Assert.True((await _workspace.CommitAllAsync(contextA, "a", "body")).Success);
        Assert.True((await _workspace.PushAsync(contextA)).IsSuccess);

        // The repository profile is edited to point at a new remote (seeded from A, as after a
        // repository migration). Only clone used to consume RemoteUrl, so the existing clone kept
        // pushing to the old remote forever.
        var remoteB = Path.Combine(_root, "remote-b.git");
        Assert.True((await _runner.RunAsync(_root, ["clone", "--bare", remoteA, remoteB])).Success);
        var contextB = NewContext(remoteB, workPath);

        Assert.True((await _workspace.PrepareAsync(contextB)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "b.sql"), "CREATE SCHEMA [b];");
        Assert.True((await _workspace.CommitAllAsync(contextB, "b", "body")).Success);
        Assert.True((await _workspace.PushAsync(contextB)).IsSuccess);

        var verifyB = Path.Combine(_root, "verify-b");
        Assert.True((await _runner.RunAsync(_root, ["clone", remoteB, verifyB])).Success);
        Assert.True(File.Exists(Path.Combine(verifyB, "schemas", "b.sql")));

        var verifyA = Path.Combine(_root, "verify-a");
        Assert.True((await _runner.RunAsync(_root, ["clone", remoteA, verifyA])).Success);
        Assert.False(File.Exists(Path.Combine(verifyA, "schemas", "b.sql")));
    }

    [Fact]
    public async Task Prepare_ForceSyncsADirtyTreeWhenTheRemoteAdvanced()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "test.sql"), "CREATE SCHEMA [app];");
        Assert.True((await _workspace.CommitAllAsync(context, "app", "body")).Success);
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);

        // Advance the remote from a second clone while the first workspace holds a crashed run's
        // modified tracked file — a plain `checkout -B` aborts with "would be overwritten".
        var otherPath = Path.Combine(_root, "other");
        var other = NewContext(remote, otherPath);
        Assert.True((await _workspace.PrepareAsync(other)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(otherPath, "schemas", "advanced.sql"), "CREATE SCHEMA [adv];");
        Assert.True((await _workspace.CommitAllAsync(other, "advanced", "body")).Success);
        Assert.True((await _workspace.PushAsync(other)).IsSuccess);

        await File.WriteAllTextAsync(Path.Combine(workPath, "schemas", "test.sql"), "-- crash residue");

        var prepared = await _workspace.PrepareAsync(context);

        Assert.True(prepared.IsSuccess, prepared.Error);
        Assert.Equal("CREATE SCHEMA [app];", await File.ReadAllTextAsync(Path.Combine(workPath, "schemas", "test.sql")));
        Assert.True(File.Exists(Path.Combine(workPath, "schemas", "advanced.sql")));
    }

    [Fact]
    public async Task Prepare_DropsCrashResidueSoItNeverReachesALaterCommit()
    {
        if (!GitAvailable())
        {
            return;
        }

        var remote = await InitBareRemoteAsync();
        var workPath = Path.Combine(_root, "work");
        var context = NewContext(remote, workPath);
        Assert.True((await _workspace.PrepareAsync(context)).IsSuccess);
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "test.sql"), "CREATE SCHEMA [app];");
        Assert.True((await _workspace.CommitAllAsync(context, "app", "body")).Success);
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);

        // A crashed run left a modified tracked file and an untracked leftover; no remote advance.
        await File.WriteAllTextAsync(Path.Combine(workPath, "schemas", "test.sql"), "-- crash residue");
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "leftover.sql"), "-- orphan");

        var prepared = await _workspace.PrepareAsync(context);

        Assert.True(prepared.IsSuccess, prepared.Error);
        Assert.Equal("CREATE SCHEMA [app];", await File.ReadAllTextAsync(Path.Combine(workPath, "schemas", "test.sql")));
        Assert.False(File.Exists(Path.Combine(workPath, "schemas", "leftover.sql")));

        // The next unrelated commit must carry only its own change, not the residue.
        await File.WriteAllTextAsync(CreateFile(workPath, "schemas", "unrelated.sql"), "CREATE SCHEMA [u];");
        Assert.True((await _workspace.CommitAllAsync(context, "unrelated", "body")).Success);
        Assert.True((await _workspace.PushAsync(context)).IsSuccess);

        var verifyPath = Path.Combine(_root, "verify");
        Assert.True((await _runner.RunAsync(_root, ["clone", remote, verifyPath])).Success);
        Assert.Equal("CREATE SCHEMA [app];", await File.ReadAllTextAsync(Path.Combine(verifyPath, "schemas", "test.sql")));
        Assert.False(File.Exists(Path.Combine(verifyPath, "schemas", "leftover.sql")));
        Assert.True(File.Exists(Path.Combine(verifyPath, "schemas", "unrelated.sql")));
    }

    [Fact]
    public void Summarize_RedactsCredentialsEmbeddedInUrls()
    {
        // Summarize output is persisted (run history, reports, support bundles); a manual proxy
        // URL embeds user:password@host and git/curl echo it verbatim in stderr.
        var summarized = GitWorkspace.Summarize(
            "fatal: unable to access 'https://github.com/x/y.git/': Failed to connect to http://alice:s3cret@proxy:8080/");

        Assert.DoesNotContain("s3cret", summarized);
        Assert.DoesNotContain("alice", summarized);
        Assert.Contains("http://***@proxy:8080/", summarized);
    }

    private static GitWorkspaceContext NewContext(string remote, string localPath) => new()
    {
        RemoteUrl = remote,
        Branch = "main",
        LocalPath = localPath,
        CommitterName = "Obsync Tests",
        CommitterEmail = "tests@obsync.local",
    };

    private async Task<string> ConfigValueAsync(string workPath, string key)
    {
        var result = await _runner.RunAsync(workPath, ["config", "--local", "--get", key]);
        Assert.True(result.Success, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private async Task<string> InitBareRemoteAsync()
    {
        var remote = Path.Combine(_root, "remote.git");
        var init = await _runner.RunAsync(_root, ["init", "--bare", "--initial-branch=main", remote]);
        Assert.True(init.Success, init.StandardError);
        return remote;
    }

    private static string CreateFile(string workPath, string folder, string name)
    {
        var dir = Path.Combine(workPath, folder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Git marks pack files read-only; clear attributes so the tree can be removed.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp workspace.
        }
    }
}
