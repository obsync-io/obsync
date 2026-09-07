using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Obsync.Git.Tests;

/// <summary>
/// Pull-request mode reuses ONE head branch per job, exercised against a throwaway local bare
/// repository that stands in for the GitHub remote.
/// </summary>
/// <remarks>
/// The head branch used to carry the run key, so every run cut a branch nothing would ever delete
/// and opened a pull request nothing would ever close. On a repository that requires approval to
/// merge — where an unmerged pull request is the normal state rather than an edge case — a daily
/// job accumulated a branch and an open pull request per day, indefinitely, each restating the same
/// proposal.
/// <para>
/// Reusing the branch means the recut head must be reconciled with the copy the remote already
/// holds. These tests pin that reconciliation, including the two properties that make it safe
/// against an enterprise repository: it never force-pushes (rulesets routinely refuse
/// non-fast-forward updates, which would wedge the mode outright), and it never discards a commit
/// somebody else put on the branch.
/// </para>
/// </remarks>
public sealed class HeadBranchReconciliationTests : IDisposable
{
    private readonly string _root;
    private readonly GitCommandRunner _runner = new(NullLogger<GitCommandRunner>.Instance);
    private readonly GitWorkspace _workspace;

    public HeadBranchReconciliationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "obsync-head-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new GitWorkspace(_runner, NullLogger<GitWorkspace>.Instance);
    }

    [Fact]
    public async Task ARunThatProposesTheSameContent_LeavesTheRemoteBranchUntouched()
    {
        // The property that keeps a reviewer's approval alive. A repository configured to dismiss
        // stale approvals dismisses them on any new commit, so restating an identical proposal
        // would throw away a review for no change at all. Nothing is pushed.
        if (!GitAvailable())
        {
            return;
        }

        var remote = await SeedRemoteAsync();
        var context = PrContext(remote);

        await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 1;");
        var afterFirst = await RemoteTipAsync(remote, context.Branch);

        var second = await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 1;");

        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(afterFirst, await RemoteTipAsync(remote, context.Branch));
    }

    [Fact]
    public async Task ARunThatProposesNewContent_FastForwardsTheSameBranch()
    {
        // One branch, still moving forward — and forward specifically, never rewritten: the
        // previous tip must remain an ancestor, which is exactly what a force push would destroy
        // and what a ruleset would refuse.
        if (!GitAvailable())
        {
            return;
        }

        var remote = await SeedRemoteAsync();
        var context = PrContext(remote);

        await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 1;");
        var afterFirst = await RemoteTipAsync(remote, context.Branch);

        var second = await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 2;");

        Assert.True(second.IsSuccess, second.Error);
        var afterSecond = await RemoteTipAsync(remote, context.Branch);
        Assert.NotEqual(afterFirst, afterSecond);
        Assert.True(await IsAncestorAsync(remote, afterFirst, afterSecond), "the push rewrote history");
        Assert.Contains("SELECT 2", await RemoteFileAsync(remote, context.Branch, "db/p1.sql"));
    }

    [Fact]
    public async Task ACommitSomebodyElsePutOnTheBranch_IsKept()
    {
        // A reviewer pushing a fixup onto a bot's branch is ordinary enterprise behaviour. Obsync's
        // scripted content still wins — it is the source of truth for these files — but their
        // commit stays in the branch's history rather than being deleted out from under them, which
        // is what a force push would have done.
        if (!GitAvailable())
        {
            return;
        }

        var remote = await SeedRemoteAsync();
        var context = PrContext(remote);
        await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 1;");

        var theirs = await PushFromAnotherCloneAsync(
            remote, context.Branch, "db/p1.sql", "-- hand edited by a reviewer");

        var next = await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 3;");

        Assert.True(next.IsSuccess, next.Error);
        Assert.Contains("SELECT 3", await RemoteFileAsync(remote, context.Branch, "db/p1.sql"));
        Assert.True(
            await IsAncestorAsync(remote, theirs, await RemoteTipAsync(remote, context.Branch)),
            "the reviewer's commit was discarded");
    }

    [Fact]
    public async Task WhenTheBaseBranchMoves_TheProposalCarriesOnlyItsOwnFiles()
    {
        // The reason the branch is still RECUT from base every run rather than merely built on. A
        // pull request's diff is computed against the merge base, so a head that had been built on
        // would show whatever moved on base as part of Obsync's own proposal.
        if (!GitAvailable())
        {
            return;
        }

        var remote = await SeedRemoteAsync();
        var context = PrContext(remote);
        await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 1;");

        await PushFromAnotherCloneAsync(remote, "main", "README.md", "# changed by somebody else");

        var next = await ProposeAsync(context, "CREATE PROC dbo.P1 AS SELECT 4;");
        Assert.True(next.IsSuccess, next.Error);

        var diff = await InspectAsync(remote, ["diff", "--name-only", $"main...{context.Branch}"]);

        Assert.Equal("db/p1.sql", diff.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public async Task AnObjectDroppedBeforeTheProposalMerged_StopsBeingProposed()
    {
        // The deletion-tombstone hazard, closed by there being exactly one open proposal.
        //
        // While the pull request sits unmerged, nothing it proposes is on base — so an object that
        // is added by one run and then dropped from SQL before a reviewer merges has its tombstone
        // retired on the grounds that its file is absent from the recut tree. With a branch per run
        // that was a real trap: the earlier pull request still carried the file, and merging it
        // would have landed an object that no longer exists, with no state row left to notice.
        //
        // One reused branch removes the trap at its root. There is only ever one open proposal, and
        // every run replaces its content wholesale with the current estate recut from base, so a
        // dropped object simply stops being proposed.
        if (!GitAvailable())
        {
            return;
        }

        var remote = await SeedRemoteAsync();
        var context = PrContext(remote);

        await ProposeAsync(context, ("db/p1.sql", "CREATE PROC dbo.P1 AS SELECT 1;"), ("db/p2.sql", "CREATE PROC dbo.P2 AS SELECT 1;"));
        Assert.Contains("db/p1.sql", await InspectAsync(remote, ["ls-tree", "-r", "--name-only", context.Branch]));

        // P1 is dropped in SQL: the next run scripts only P2.
        var second = await ProposeAsync(context, ("db/p2.sql", "CREATE PROC dbo.P2 AS SELECT 1;"));

        Assert.True(second.IsSuccess, second.Error);
        var tree = await InspectAsync(remote, ["ls-tree", "-r", "--name-only", context.Branch]);
        Assert.DoesNotContain("db/p1.sql", tree);
        Assert.Contains("db/p2.sql", tree);
    }

    /// <summary>One run: recut the head off base, write the script, commit, push.</summary>
    private Task<Obsync.Shared.Results.Result> ProposeAsync(GitWorkspaceContext context, string body) =>
        ProposeAsync(context, ("db/p1.sql", body));

    /// <summary>One run scripting an explicit set of files — everything else is left off the tree.</summary>
    private async Task<Obsync.Shared.Results.Result> ProposeAsync(
        GitWorkspaceContext context, params (string Path, string Body)[] files)
    {
        var prepared = await _workspace.PrepareAsync(context);
        Assert.True(prepared.IsSuccess, prepared.Error);

        foreach (var (path, body) in files)
        {
            var file = Path.Combine(context.LocalPath, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, body);
        }

        var commit = await _workspace.CommitAllAsync(context, "Obsync proposal", "body");
        Assert.True(commit.Success, commit.Error);

        return await _workspace.PushAsync(context);
    }

    /// <summary>A bare remote with a main branch, which pull-request mode requires to exist.</summary>
    private async Task<string> SeedRemoteAsync()
    {
        var remote = Path.Combine(_root, "remote.git");
        Assert.True((await _runner.RunAsync(_root, ["init", "--bare", "--initial-branch=main", remote])).Success);

        var seedPath = Path.Combine(_root, "seed");
        var seed = new GitWorkspaceContext
        {
            RemoteUrl = remote,
            Branch = "main",
            LocalPath = seedPath,
            CommitterName = "Obsync Tests",
            CommitterEmail = "tests@obsync.local",
        };
        Assert.True((await _workspace.PrepareAsync(seed)).IsSuccess);
        await File.WriteAllTextAsync(Path.Combine(seedPath, "README.md"), "# repo");
        Assert.True((await _workspace.CommitAllAsync(seed, "seed", "body")).Success);
        Assert.True((await _workspace.PushAsync(seed)).IsSuccess);
        return remote;
    }

    private GitWorkspaceContext PrContext(string remote) => new()
    {
        RemoteUrl = remote,
        Branch = "obsync/a-job/3f2a1b9c",
        BaseBranch = "main",
        LocalPath = Path.Combine(_root, "work"),
        CommitterName = "Obsync Tests",
        CommitterEmail = "tests@obsync.local",
    };

    /// <summary>Commits and pushes one file to a branch from a clone that is not Obsync's.</summary>
    private async Task<string> PushFromAnotherCloneAsync(string remote, string branch, string path, string content)
    {
        var other = Path.Combine(_root, $"other-{Guid.NewGuid():N}");
        Assert.True((await _runner.RunAsync(_root, ["clone", remote, other])).Success);
        Assert.True((await _runner.RunAsync(other, ["checkout", "-B", branch, $"origin/{branch}"])).Success);

        var file = Path.Combine(other, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content);

        Assert.True((await _runner.RunAsync(other, ["add", "-A"])).Success);
        Assert.True((await _runner.RunAsync(
            other,
            ["-c", "user.name=Someone", "-c", "user.email=someone@example.com", "commit", "-m", "by hand"])).Success);
        Assert.True((await _runner.RunAsync(other, ["push", "origin", branch])).Success);

        return (await _runner.RunAsync(other, ["rev-parse", "HEAD"])).StandardOutput.Trim();
    }

    private Task<string> RemoteTipAsync(string remote, string branch) =>
        InspectAsync(remote, ["rev-parse", branch]);

    private Task<string> RemoteFileAsync(string remote, string branch, string path) =>
        InspectAsync(remote, ["show", $"{branch}:{path}"]);

    private async Task<bool> IsAncestorAsync(string remote, string ancestor, string descendant) =>
        (await _runner.RunAsync(remote, ["merge-base", "--is-ancestor", ancestor.Trim(), descendant.Trim()])).Success;

    private async Task<string> InspectAsync(string remote, IReadOnlyList<string> args)
    {
        var result = await _runner.RunAsync(remote, args);
        Assert.True(result.Success, result.StandardError);
        return result.StandardOutput.Trim();
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
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
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A lingering git handle must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
