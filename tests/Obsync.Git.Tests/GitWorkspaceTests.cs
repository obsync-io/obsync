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

    private static GitWorkspaceContext NewContext(string remote, string localPath) => new()
    {
        RemoteUrl = remote,
        Branch = "main",
        LocalPath = localPath,
        CommitterName = "Obsync Tests",
        CommitterEmail = "tests@obsync.local",
    };

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
