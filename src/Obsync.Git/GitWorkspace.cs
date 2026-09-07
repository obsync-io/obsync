using Microsoft.Extensions.Logging;
using Obsync.Shared;
using Obsync.Shared.Results;

namespace Obsync.Git;

/// <summary>Everything needed to operate on a local Git workspace for one repository/branch.</summary>
public sealed class GitWorkspaceContext
{
    public required string RemoteUrl { get; init; }

    /// <summary>The branch that is checked out, committed to, and pushed. In pull-request mode this is
    /// the per-run head branch; in direct-commit mode it is the target branch.</summary>
    public required string Branch { get; init; }

    /// <summary>
    /// When set, pull-request mode: <see cref="Branch"/> is created fresh off this base branch each
    /// run (the base is the PR target and must already exist on the remote). Null = direct-commit mode.
    /// </summary>
    public string? BaseBranch { get; init; }

    public required string LocalPath { get; init; }

    /// <summary>Full HTTP header value used for authentication, e.g. "AUTHORIZATION: basic &lt;base64&gt;". Never logged.</summary>
    public string? AuthorizationHeader { get; init; }

    public string CommitterName { get; init; } = "Obsync";
    public string CommitterEmail { get; init; } = "obsync@localhost";

    /// <summary>Number of attempts (1 = no retry) for transient network operations (clone/fetch/push).</summary>
    public int NetworkRetryCount { get; init; } = 3;

    /// <summary>
    /// HTTP/HTTPS proxy URL for network operations (may embed credentials); null for a direct
    /// connection. Injected per-command via <c>GIT_CONFIG_*</c> environment variables, never
    /// written to <c>.git/config</c>.
    /// </summary>
    public string? ProxyUrl { get; init; }

    /// <summary>Which TLS stack the bundled git validates certificates with.</summary>
    public GitTlsBackend TlsBackend { get; init; } = GitTlsBackend.Default;

    /// <summary>PEM CA bundle for the OpenSSL backend; ignored by the schannel backends.</summary>
    public string? CaBundlePath { get; init; }
}

/// <summary>The outcome of a commit attempt.</summary>
public sealed record GitCommitResult(bool HadChanges, string? CommitSha, bool Success, string? Error)
{
    public static GitCommitResult NoChanges() => new(false, null, true, null);
    public static GitCommitResult Committed(string sha) => new(true, sha, true, null);
    public static GitCommitResult Failed(string error) => new(false, null, false, error);
}

/// <summary>Manages a local Git working tree: prepare (clone/fetch/checkout), commit, and push.</summary>
public interface IGitWorkspace
{
    Task<Result> PrepareAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);

    Task<GitCommitResult> CommitAllAsync(
        GitWorkspaceContext context, string subject, string body, bool allowEmpty = false, CancellationToken cancellationToken = default);

    Task<Result> PushAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the local branch has commits that are not yet on the remote — e.g. a previous run
    /// committed but its push failed. Lets the engine re-push instead of losing the work.
    /// </summary>
    Task<bool> HasUnpushedCommitsAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a description of the first of <paramref name="relativePaths"/> that git is ignoring
    /// — including the ignore rule and the file it came from — or <c>null</c> when git would track
    /// them all. Used to tell "the tree really was identical" apart from "git never saw these
    /// files", two states that are otherwise indistinguishable from a commit that staged nothing.
    /// </summary>
    Task<string?> FindIgnoredPathAsync(
        GitWorkspaceContext context, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitWorkspace" />
public sealed partial class GitWorkspace : IGitWorkspace
{
    private readonly IGitCommandRunner _git;
    private readonly ILogger<GitWorkspace> _logger;

    public GitWorkspace(IGitCommandRunner git, ILogger<GitWorkspace> logger)
    {
        _git = git;
        _logger = logger;
    }

    public async Task<Result> PrepareAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default)
    {
        var gitDir = Path.Combine(context.LocalPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            var cloned = await CloneFreshAsync(context, cancellationToken).ConfigureAwait(false);
            if (cloned.IsFailure)
            {
                return cloned;
            }
        }
        else
        {
            // A cancellation kills the git process tree mid-command; a kill during add/commit
            // strands .git/index.lock and every later index operation fails with "File exists".
            // Obsync serializes all workspace access via a cross-process per-repository lock, so
            // any lock file that exists here is stale by construction and safe to delete.
            RemoveStaleLocks(gitDir);

            // Only clone consumes RemoteUrl, so an edited repository profile would keep pushing to
            // the old remote forever. Idempotent and local.
            //
            // NOT best-effort any more. It was, and a stranded config.lock made it fail silently —
            // which quietly reinstated the exact bug this line exists to fix, with every subsequent
            // run pushing to the previous remote. A remote we cannot set is a remote we cannot
            // trust, so it fails the run instead.
            var setUrl = await _git.RunAsync(
                context.LocalPath, ["remote", "set-url", "origin", context.RemoteUrl], cancellationToken).ConfigureAwait(false);
            if (!setUrl.Success)
            {
                // Two very different causes, and only one is fatal here. A CORRUPT workspace fails
                // this too, and it must still reach the fetch below — that is what detects the
                // corruption and recreates the clone. Failing outright would turn a self-healing
                // condition into a permanent one, which the recovery test catches.
                //
                // On a HEALTHY workspace there is no such excuse: the cause is a stranded
                // config.lock or a permissions problem, and continuing would push to whatever remote
                // the config still names. That is the silent-wrong-remote bug this call exists to
                // prevent, so it fails instead of being swallowed as it used to be.
                var healthy = (await _git.RunAsync(context.LocalPath, ["rev-parse", "--git-dir"], cancellationToken)
                    .ConfigureAwait(false)).Success;
                if (healthy)
                {
                    return Result.Failure(
                        $"git could not point the workspace at the repository's remote: {Summarize(setUrl.StandardError)}");
                }

                _logger.LogWarning(
                    "Could not set the remote on {Path}; the workspace looks corrupt and will be recreated.",
                    context.LocalPath);
            }

            // Clones deployed before these repository defaults existed (see CloneFreshAsync) must
            // pick them up too. `git config` set is a cheap local write, fine on every prepare;
            // best-effort for the same reason as set-url above.
            _ = await _git.RunAsync(
                context.LocalPath, ["config", "core.longpaths", "true"], cancellationToken).ConfigureAwait(false);
            _ = await _git.RunAsync(
                context.LocalPath, ["config", "feature.manyFiles", "true"], cancellationToken).ConfigureAwait(false);
            _ = await _git.RunAsync(
                context.LocalPath, ["config", "core.fsyncMethod", "batch"], cancellationToken).ConfigureAwait(false);
            EnsureObsyncTmpExcluded(context.LocalPath);

            var fetch = await RunNetworkAsync(context.LocalPath, context, ["fetch", "origin"], cancellationToken).ConfigureAwait(false);
            if (!fetch.Success)
            {
                // Distinguish "network/auth problem" (surface it) from "the clone itself is broken"
                // (a crash or kill mid-clone corrupts .git) — a broken workspace would otherwise
                // fail every future run until someone manually deletes an internal folder. The
                // workspace is fully regenerable, so delete and clone fresh.
                var healthy = (await _git.RunAsync(context.LocalPath, ["rev-parse", "--git-dir"], cancellationToken)
                    .ConfigureAwait(false)).Success;
                if (healthy)
                {
                    return Result.Failure($"git fetch failed: {Summarize(fetch.StandardError)}");
                }

                _logger.LogWarning(
                    "The git workspace at {Path} is corrupt (likely an interrupted clone); recreating it.",
                    context.LocalPath);
                var recloned = await CloneFreshAsync(context, cancellationToken).ConfigureAwait(false);
                if (recloned.IsFailure)
                {
                    return recloned;
                }
            }
        }

        // Pull request mode: create a fresh per-run head branch at the base branch's tip. The base
        // must already exist on the remote (it's what the PR targets); the per-run head does not, so
        // the direct-mode stranded-commit preservation below does not apply.
        if (context.BaseBranch is not null)
        {
            var baseExists = (await _git.RunAsync(
                context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.BaseBranch}"], cancellationToken)
                .ConfigureAwait(false)).Success;
            if (!baseExists)
            {
                return Result.Failure($"The base branch '{context.BaseBranch}' does not exist on the remote.");
            }

            var headCheckout = await _git.RunAsync(
                context.LocalPath, ["checkout", "-B", context.Branch, $"origin/{context.BaseBranch}"], cancellationToken).ConfigureAwait(false);
            return headCheckout.Success
                ? Result.Success()
                : Result.Failure($"git checkout failed: {Summarize(headCheckout.StandardError)}");
        }

        var remoteBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;

        var localBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/heads/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;

        // If the local branch already carries commits that never reached the remote (a prior push
        // failed), DO NOT hard-reset to origin — that would silently discard the committed changes.
        // Just make sure we're on the branch; the engine will re-push the pending commit(s).
        if (localBranchExists
            && await IsAheadOfOriginAsync(context.LocalPath, context.Branch, remoteBranchExists, cancellationToken).ConfigureAwait(false))
        {
            var stay = await _git.RunAsync(context.LocalPath, ["checkout", context.Branch], cancellationToken).ConfigureAwait(false);
            return stay.Success
                ? Result.Success()
                : Result.Failure($"git checkout failed: {Summarize(stay.StandardError)}");
        }

        // Otherwise sync the local branch to origin (picking up any remote changes and healing
        // drift). Nothing local is precious on this path — no unpushed commits, and the working
        // tree is regenerated from SQL Server every run — so sync forcefully: a crashed run can
        // leave modified tracked files that a plain checkout refuses to overwrite when the remote
        // advanced ("would be overwritten"), and untracked residue that would otherwise leak into
        // the next commit.
        if (remoteBranchExists)
        {
            var checkout = await _git.RunAsync(
                context.LocalPath, ["checkout", "-f", "-B", context.Branch, $"origin/{context.Branch}"], cancellationToken)
                .ConfigureAwait(false);
            if (!checkout.Success)
            {
                return Result.Failure($"git checkout failed: {Summarize(checkout.StandardError)}");
            }

            var clean = await _git.RunAsync(context.LocalPath, ["clean", "-fd"], cancellationToken).ConfigureAwait(false);
            return clean.Success
                ? Result.Success()
                : Result.Failure($"git clean failed: {Summarize(clean.StandardError)}");
        }

        // No remote branch yet (first run against an empty remote): HEAD may be unborn, so a
        // forced checkout/clean has nothing to sync against.
        var create = await _git.RunAsync(context.LocalPath, ["checkout", "-B", context.Branch], cancellationToken).ConfigureAwait(false);
        return create.Success
            ? Result.Success()
            : Result.Failure($"git checkout failed: {Summarize(create.StandardError)}");
    }

    /// <summary>
    /// Deletes any remnant at the workspace path (a cancelled or killed clone leaves a partial
    /// directory that makes every later clone fail with "destination path already exists") and
    /// clones fresh. Everything in the workspace is regenerable — from the remote and from SQL
    /// Server — so deletion is always safe here.
    /// </summary>
    private async Task<Result> CloneFreshAsync(GitWorkspaceContext context, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(context.LocalPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // `clone -c` applies each setting during the clone itself AND persists it into the new
        // clone's config:
        // - core.longpaths: Windows MAX_PATH protection — a deep workspaces root plus a long
        //   schema/object path can exceed 260 chars. It must be active during the clone's own
        //   checkout: a self-heal re-clone of a repo that already contains such paths would fail
        //   with "Filename too long" before any post-clone config could apply.
        // - feature.manyFiles: index.version=4 + core.untrackedCache=true — smaller index and
        //   cached untracked-walks over large exported trees (a VLDB run writes 100k+ script files).
        // - core.fsyncMethod=batch: batches loose-object fsyncs on mass adds; the reduced
        //   durability window is acceptable because the workspace is fully regenerable (above).
        var clone = await RunNetworkAsync(
            parent ?? ".", context,
            [
                "clone",
                "-c", "core.longpaths=true",
                "-c", "feature.manyFiles=true",
                "-c", "core.fsyncMethod=batch",
                context.RemoteUrl, context.LocalPath,
            ],
            cancellationToken,
            // Per ATTEMPT, not once: a clone that fails partway still leaves a destination behind,
            // and git refuses to clone into an existing path. Everything here is regenerable — from
            // the remote and from SQL Server — so deleting it is always safe.
            beforeAttempt: () =>
            {
                if (Directory.Exists(context.LocalPath))
                {
                    DeleteDirectory(context.LocalPath);
                }
            }).ConfigureAwait(false);
        if (!clone.Success)
        {
            return Result.Failure($"git clone failed: {Summarize(clone.StandardError)}");
        }

        EnsureObsyncTmpExcluded(context.LocalPath);
        return Result.Success();
    }

    // The engine's atomic-write temp files (*.obsync-tmp, orphaned only by a hard process kill
    // mid-write) are kept out of commits and status via .git/info/exclude rather than an exclude
    // pathspec on add/status: git bypasses the untracked cache for any non-empty pathspec.
    // Idempotent check-then-append; info/exclude is local-only and never pushed.
    private static void EnsureObsyncTmpExcluded(string localPath)
    {
        const string pattern = "*.obsync-tmp";
        var excludePath = Path.Combine(localPath, ".git", "info", "exclude");
        if (File.Exists(excludePath))
        {
            var content = File.ReadAllText(excludePath);
            if (content.Split('\n').Any(line => line.Trim() == pattern))
            {
                return;
            }

            var separator = content.Length == 0 || content.EndsWith('\n') ? string.Empty : Environment.NewLine;
            File.AppendAllText(excludePath, separator + pattern + Environment.NewLine);
            return;
        }

        Directory.CreateDirectory(Path.Combine(localPath, ".git", "info"));
        File.WriteAllText(excludePath, pattern + Environment.NewLine);
    }

    // Git object files are read-only, which makes Directory.Delete(recursive) throw — clear the
    // attribute first.
    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    public async Task<bool> HasUnpushedCommitsAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default)
    {
        var remoteBranchExists = (await _git.RunAsync(
            context.LocalPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false)).Success;
        return await IsAheadOfOriginAsync(context.LocalPath, context.Branch, remoteBranchExists, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the branch carries local commits that are not yet on origin — or when that cannot
    /// be determined.
    /// </summary>
    /// <remarks>
    /// The unknown case is deliberately folded in with "yes". This answer gates the destructive
    /// branch of <see cref="PrepareAsync"/> (<c>checkout -f -B</c> onto origin, then
    /// <c>clean -fd</c>), so reading a failed <c>rev-list</c> as "nothing unpushed" would discard a
    /// commit that only exists here — the exact loss the gate was written to prevent. Erring the
    /// other way costs at most a redundant push of an already-current branch, which git answers
    /// with "Everything up-to-date".
    /// </remarks>
    private async Task<bool> IsAheadOfOriginAsync(
        string localPath, string branch, bool remoteBranchExists, CancellationToken cancellationToken)
    {
        // With no remote branch every local commit is un-pushed; otherwise count origin/branch..branch.
        var range = remoteBranchExists ? $"origin/{branch}..{branch}" : branch;
        var result = await _git.RunAsync(localPath, ["rev-list", "--count", range], cancellationToken).ConfigureAwait(false);

        if (result.Success && int.TryParse(result.StandardOutput.Trim(), out var count))
        {
            return count > 0;
        }

        _logger.LogWarning(
            "Could not count unpushed commits on '{Branch}' ({Error}); assuming there are some so that " +
            "nothing local is discarded.", branch, Summarize(result.StandardError));
        return true;
    }

    public async Task<GitCommitResult> CommitAllAsync(
        GitWorkspaceContext context, string subject, string body, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        // Full-tree sweep: `add -A -- .` stages every creation, modification, and deletion under
        // the workspace. *.obsync-tmp temps stay out via .git/info/exclude (see
        // EnsureObsyncTmpExcluded), and the untracked cache enabled by feature.manyFiles (see
        // CloneFreshAsync) makes the walk skip unchanged directories on subsequent runs.
        var add = await _git.RunAsync(context.LocalPath, ["add", "-A", "--", "."], cancellationToken).ConfigureAwait(false);
        if (!add.Success)
        {
            return GitCommitResult.Failed($"git add failed: {Summarize(add.StandardError)}");
        }

        // `status --porcelain` prints one line per change (~100 MB of buffered stdout on a
        // million-file first run) just to answer yes/no; `diff --cached --quiet` answers with the
        // exit code alone: 0 = nothing staged, 1 = staged changes, anything else = error. Holds on
        // an unborn HEAD too (first commit against an empty remote).
        var diff = await _git.RunAsync(context.LocalPath, ["diff", "--cached", "--quiet"], cancellationToken).ConfigureAwait(false);
        if (diff.ExitCode is not (0 or 1))
        {
            return GitCommitResult.Failed($"git diff failed: {Summarize(diff.StandardError)}");
        }

        var hasStagedChanges = diff.ExitCode == 1;
        if (!hasStagedChanges && !allowEmpty)
        {
            return GitCommitResult.NoChanges();
        }

        var commitArgs = new List<string>
        {
            "-c", $"user.name={context.CommitterName}",
            "-c", $"user.email={context.CommitterEmail}",
            // Unattended commits are never signed. With commit.gpgsign=true in the account's config
            // git invokes gpg, and a passphrase-protected key then waits on a pinentry dialog that
            // has no desktop to appear on under the service. Measured: gpg missing fails the commit
            // outright, gpg present and blocking hangs it. Obsync's commits carry no signing identity
            // to offer, so there is nothing to lose by declining.
            "commit", "--no-gpg-sign", "-m", subject, "-m", body,
        };
        if (!hasStagedChanges)
        {
            commitArgs.Add("--allow-empty");
        }

        var commit = await _git.RunAsync(context.LocalPath, commitArgs, cancellationToken).ConfigureAwait(false);
        if (!commit.Success)
        {
            return GitCommitResult.Failed($"git commit failed: {Summarize(commit.StandardError)}");
        }

        var rev = await _git.RunAsync(context.LocalPath, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        var sha = rev.StandardOutput.Trim();
        _logger.LogInformation("Created commit {Sha} on {Branch}.", sha, context.Branch);
        return GitCommitResult.Committed(sha);
    }

    /// <summary>
    /// Deletes every stale git lock file in a workspace Obsync is about to use.
    /// </summary>
    /// <remarks>
    /// Obsync serialises all workspace access through a cross-process per-repository lock, so any
    /// git lock file present at this point is stale by construction — the process that created it is
    /// gone. And they ARE created: git process trees are killed on cancellation and on the command
    /// timeout, mid-write.
    /// <para>
    /// Only <c>index.lock</c> used to be cleaned, which left the other two unrecoverable. A stranded
    /// <c>refs/heads/&lt;branch&gt;.lock</c> fails <c>checkout -f -B</c> with "cannot lock ref" on
    /// every run FOREVER, and it is invisible to the corrupt-workspace self-heal below because that
    /// is gated on fetch failing — measured: with the ref lock present, <c>rev-parse --git-dir</c>
    /// and <c>fetch</c> both exit 0, so nothing triggers a reclone. A stranded <c>config.lock</c>
    /// fails every <c>git config</c> write, including the <c>remote set-url</c> above.
    /// </para>
    /// </remarks>
    private void RemoveStaleLocks(string gitDir)
    {
        foreach (var lockFile in EnumerateLockFiles(gitDir))
        {
            try
            {
                File.SetAttributes(lockFile, FileAttributes.Normal);
                File.Delete(lockFile);
                _logger.LogWarning("Removed a stale git lock left by an interrupted run: {Path}", lockFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Report nothing here: the command that needs it will fail with git's own message,
                // which is more specific than anything this loop could say.
                _logger.LogWarning(ex, "Could not remove a stale git lock: {Path}", lockFile);
            }
        }
    }

    private static IEnumerable<string> EnumerateLockFiles(string gitDir)
    {
        foreach (var name in (string[])["index.lock", "config.lock", "HEAD.lock", "packed-refs.lock"])
        {
            var path = Path.Combine(gitDir, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        // refs/**/*.lock — one per ref git was mid-update on. Enumerated rather than named because
        // the branch is per-job and PR mode cuts a fresh one every run.
        var refs = Path.Combine(gitDir, "refs");
        if (!Directory.Exists(refs))
        {
            yield break;
        }

        string[] refLocks;
        try
        {
            refLocks = Directory.GetFiles(refs, "*.lock", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in refLocks)
        {
            yield return path;
        }
    }

    /// <summary>Most paths handed to one <c>check-ignore</c>; enough to catch a rule, short enough for argv.</summary>
    private const int IgnoreProbeSampleSize = 32;

    public async Task<string?> FindIgnoredPathAsync(
        GitWorkspaceContext context, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePaths.Count == 0)
        {
            return null;
        }

        // -v prints "<source>:<line>:<pattern>\t<path>", which names the .gitignore and the rule —
        // the difference between an error the user can act on and one they cannot. Exit codes:
        // 0 = at least one path is ignored, 1 = none are, anything else = a real failure, which is
        // reported as "not ignored" so a broken probe can never fail an otherwise healthy run.
        var args = new List<string> { "check-ignore", "-v", "--" };
        args.AddRange(relativePaths.Take(IgnoreProbeSampleSize));

        var result = await _git.RunAsync(context.LocalPath, args, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.ExitCode != 1)
            {
                _logger.LogWarning(
                    "git check-ignore exited {ExitCode} in {Path}: {Error}",
                    result.ExitCode, context.LocalPath, Summarize(result.StandardError));
            }

            return null;
        }

        var first = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    public async Task<Result> PushAsync(GitWorkspaceContext context, CancellationToken cancellationToken = default)
    {
        var push = await RunNetworkAsync(
            context.LocalPath, context, ["push", "-u", "origin", context.Branch], cancellationToken).ConfigureAwait(false);
        if (push.Success)
        {
            return Result.Success();
        }

        // A push that could not COMMUNICATE is not a push that did not happen. git can send the pack,
        // the server can accept it and update the ref, and the connection can drop before the reply
        // arrives — or our own timeout can kill git mid-conversation. From here those are
        // indistinguishable from "nothing was sent", and reading them as failure has a cost: the run
        // reports a failure for work that landed, and in pull-request mode the next run cuts another
        // head branch and pushes the same content again.
        //
        // Only genuinely ambiguous outcomes are worth the question. A rejection is not ambiguous —
        // the server answered — so a protected branch, a ruleset or a non-fast-forward goes straight
        // to failure without a wasted round trip.
        if (push.ExitCode == GitCommandRunner.TimedOutExitCode
            || GitTransientErrors.IsTransient(push.StandardError))
        {
            if (await RemoteMatchesLocalHeadAsync(context, cancellationToken).ConfigureAwait(false) == true)
            {
                _logger.LogInformation(
                    "git push reported a failure on {Branch}, but the remote already carries this commit — "
                    + "the push landed and its reply was lost.", context.Branch);
                return Result.Success();
            }
        }

        return Result.Failure($"git push failed: {Summarize(push.StandardError)}");
    }

    /// <summary>
    /// Whether origin's copy of the branch is already at our HEAD: true, false, or null when it
    /// could not be established.
    /// </summary>
    /// <remarks>
    /// Asks the SERVER, not the local remote-tracking ref. <c>refs/remotes/origin/&lt;branch&gt;</c> is
    /// updated by a push that git saw succeed, so on the path that matters here it is precisely the
    /// thing that is stale — using it would answer "not pushed" for the case this exists to detect.
    /// <para>
    /// Null is returned for anything inconclusive, and the caller must treat null as "not confirmed".
    /// Reporting a failure that actually succeeded costs a duplicate branch; reporting a success that
    /// did not costs delivered-marked work that never arrived, which is the worse trade by a distance.
    /// </para>
    /// </remarks>
    private async Task<bool?> RemoteMatchesLocalHeadAsync(
        GitWorkspaceContext context, CancellationToken cancellationToken)
    {
        var head = await _git.RunAsync(context.LocalPath, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!head.Success)
        {
            return null;
        }

        var local = head.StandardOutput.Trim();
        if (local.Length == 0)
        {
            return null;
        }

        var remote = await RunNetworkAsync(
            context.LocalPath, context, ["ls-remote", "origin", $"refs/heads/{context.Branch}"], cancellationToken)
            .ConfigureAwait(false);
        if (!remote.Success)
        {
            return null;
        }

        // "<sha>\trefs/heads/<branch>", or empty when the branch does not exist on the remote.
        var line = remote.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
        {
            return false;
        }

        var sha = line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return sha is { Length: > 0 } && string.Equals(sha, local, StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="beforeAttempt">
    /// Run before EVERY attempt, including retries. Clone needs this: its destination cleanup used
    /// to sit above the retry loop, so attempts 2 and 3 ran against whatever attempt 1 had partially
    /// created and failed with "destination path already exists" — making clone retries strictly
    /// less reliable than fetch and push retries, which have no such state.
    /// </param>
    private async Task<GitCommandResult> RunNetworkAsync(
        string workingDirectory, GitWorkspaceContext context, IReadOnlyList<string> args,
        CancellationToken cancellationToken, Action? beforeAttempt = null)
    {
        // Built by GitNetworkEnvironment so this call and the preflight reachability probe are
        // configured identically — same auth scoping, same proxy, same TLS backend. A probe that
        // differs from the run it vouches for is how five green ticks preceded a failing clone.
        var environment = GitNetworkEnvironment.Build(new GitNetworkOptions(
            context.RemoteUrl, context.AuthorizationHeader, context.ProxyUrl, context.TlsBackend, context.CaBundlePath));

        var maxAttempts = Math.Max(1, context.NetworkRetryCount);
        var attempt = 0;
        while (true)
        {
            attempt++;
            beforeAttempt?.Invoke();
            var result = await _git.RunAsync(workingDirectory, args, environment, cancellationToken).ConfigureAwait(false);
            if (result.Success || attempt >= maxAttempts || !GitTransientErrors.IsTransient(result.StandardError))
            {
                return result;
            }

            _logger.LogWarning(
                "Transient git network failure on '{Op}' (attempt {Attempt}/{Max}); retrying.",
                args.Count > 0 ? args[0] : "?", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Condenses git stderr into persistable failure text. These strings outlive the run
    /// (runs.error_message, run logs, reports, support bundles), so any credential a tool echoed back
    /// is scrubbed first — see <see cref="SecretRedactor"/> for which shapes and why. Current git
    /// strips URL userinfo before echoing it, but that is git's choice, not a guarantee this code can
    /// make: the scrub is what makes it one. Internal for tests.
    /// </summary>
    /// <summary>
    /// How much of git's stderr survives. 500 was too tight for the message that matters most: a
    /// GitHub rule rejection prints the code first, then the URL, then one bullet per violated rule,
    /// and the bullets are the only part that says what to do about it. Truncating mid-way left the
    /// diagnosis dependent on how verbose GitHub happened to be — and dropped the actionable half.
    /// Still bounded, because this text is persisted (runs.error_message, run logs, reports, support
    /// bundles) and an unbounded error would be copied into all of them.
    /// </summary>
    private const int MaxSummaryLength = 2000;

    internal static string Summarize(string error)
    {
        var redacted = SecretRedactor.Scrub(error.Trim()) ?? string.Empty;
        return redacted.Length <= MaxSummaryLength ? redacted : redacted[..MaxSummaryLength] + "…";
    }
}
