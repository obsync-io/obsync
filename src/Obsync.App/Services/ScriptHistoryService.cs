using System.IO;
using Obsync.Data.Repositories;
using Obsync.Git;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// The old and new content of a scripted object at a commit, or a human-readable reason why the
/// content is not retrievable on this machine.
/// </summary>
public sealed record ScriptVersionsResult
{
    private ScriptVersionsResult(bool isAvailable, string oldContent, string newContent, string? unavailableReason)
    {
        IsAvailable = isAvailable;
        OldContent = oldContent;
        NewContent = newContent;
        UnavailableReason = unavailableReason;
    }

    public bool IsAvailable { get; }

    /// <summary>The content before the commit; empty for an added object or the first commit.</summary>
    public string OldContent { get; }

    /// <summary>The content at the commit; empty for a deleted object.</summary>
    public string NewContent { get; }

    /// <summary>Why the content could not be retrieved, phrased for the UI; null when available.</summary>
    public string? UnavailableReason { get; }

    public static ScriptVersionsResult Available(string oldContent, string newContent) =>
        new(true, oldContent, newContent, null);

    public static ScriptVersionsResult Unavailable(string reason) =>
        new(false, string.Empty, string.Empty, reason);
}

/// <summary>One committed version of a scripted object's file, for the history rail.</summary>
public sealed record ScriptFileVersion(string Sha, DateTimeOffset Date, string Author, string Subject)
{
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];
}

/// <summary>A file's committed versions (newest first), or why they are not retrievable here.</summary>
public sealed record ScriptFileHistoryResult
{
    private ScriptFileHistoryResult(bool isAvailable, IReadOnlyList<ScriptFileVersion> versions, string? unavailableReason)
    {
        IsAvailable = isAvailable;
        Versions = versions;
        UnavailableReason = unavailableReason;
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<ScriptFileVersion> Versions { get; }

    public string? UnavailableReason { get; }

    public static ScriptFileHistoryResult Available(IReadOnlyList<ScriptFileVersion> versions) =>
        new(true, versions, null);

    public static ScriptFileHistoryResult Unavailable(string reason) =>
        new(false, [], reason);
}

/// <summary>
/// Reads a scripted object's before/after content for a commit — and its full committed version
/// list — from the repository's LOCAL git workspace (the same clone the sync engine commits from).
/// Never touches the network: when the workspace or the commit is missing locally it reports
/// "unavailable" with a reason instead.
/// </summary>
public interface IScriptHistoryService
{
    Task<ScriptVersionsResult> GetVersionsAsync(
        GitRepositoryProfile repository, string commitSha, string relativePath, ChangeType changeType,
        CancellationToken cancellationToken = default);

    /// <summary>Every committed version of one file, newest first (follows renames), capped.</summary>
    Task<ScriptFileHistoryResult> GetFileHistoryAsync(
        GitRepositoryProfile repository, string relativePath, int limit = 100,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IScriptHistoryService" />
public sealed class ScriptHistoryService : IScriptHistoryService
{
    private readonly IGitCommandRunner _git;
    private readonly IAppSettingsRepository _settings;
    private readonly string _defaultWorkspacesRoot;

    public ScriptHistoryService(IGitCommandRunner git, IAppSettingsRepository settings, string defaultWorkspacesRoot)
    {
        _git = git;
        _settings = settings;
        _defaultWorkspacesRoot = defaultWorkspacesRoot;
    }

    private const string NotSyncedReason =
        "This repository hasn't been synced on this machine yet, so there is no local copy of the scripts.";

    public async Task<ScriptVersionsResult> GetVersionsAsync(
        GitRepositoryProfile repository, string commitSha, string relativePath, ChangeType changeType,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ResolveWorkspaceAsync(repository, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return ScriptVersionsResult.Unavailable(NotSyncedReason);
        }

        // ArgumentList passes the braces literally (no shell), which is exactly what rev-parse needs.
        var verify = await _git.RunAsync(
            workspace, ["rev-parse", "--verify", "--quiet", $"{commitSha}^{{commit}}"], cancellationToken)
            .ConfigureAwait(false);
        if (!verify.Success)
        {
            return ScriptVersionsResult.Unavailable(
                $"Commit {Short(commitSha)} isn't present in the local copy of this repository.");
        }

        var newContent = string.Empty;
        if (changeType != ChangeType.Deleted)
        {
            var show = await _git.RunAsync(workspace, ["show", $"{commitSha}:{relativePath}"], cancellationToken)
                .ConfigureAwait(false);
            if (!show.Success)
            {
                return ScriptVersionsResult.Unavailable(
                    $"'{relativePath}' could not be read from commit {Short(commitSha)}.");
            }

            newContent = show.StandardOutput;
        }

        var oldContent = string.Empty;
        if (changeType != ChangeType.Added)
        {
            // The parent may not exist (first commit) or may not contain the file; for a modified
            // object both simply mean "no previous version", so the diff degrades to all-new.
            var show = await _git.RunAsync(workspace, ["show", $"{commitSha}^:{relativePath}"], cancellationToken)
                .ConfigureAwait(false);
            if (show.Success)
            {
                oldContent = show.StandardOutput;
            }
            else if (changeType == ChangeType.Deleted)
            {
                return ScriptVersionsResult.Unavailable(
                    $"The previous version of '{relativePath}' could not be read from the local copy.");
            }
        }

        return ScriptVersionsResult.Available(oldContent, newContent);
    }

    public async Task<ScriptFileHistoryResult> GetFileHistoryAsync(
        GitRepositoryProfile repository, string relativePath, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ResolveWorkspaceAsync(repository, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return ScriptFileHistoryResult.Unavailable(NotSyncedReason);
        }

        // One record per line; \x1f (unit separator) never appears in shas, ISO dates, or sane
        // author names, and a subject containing it just loses its tail (parsed defensively below).
        var log = await _git.RunAsync(
            workspace,
            ["log", "--follow", $"--max-count={limit}", "--format=%H%x1f%aI%x1f%an%x1f%s", "--", relativePath],
            cancellationToken).ConfigureAwait(false);
        if (!log.Success)
        {
            return ScriptFileHistoryResult.Unavailable(
                $"The history of '{relativePath}' could not be read from the local copy.");
        }

        var versions = new List<ScriptFileVersion>();
        foreach (var line in log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\x1f');
            if (parts.Length < 4 || parts[0].Length < 7
                || !DateTimeOffset.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var date))
            {
                continue;
            }

            versions.Add(new ScriptFileVersion(parts[0], date, parts[2], parts[3]));
        }

        return ScriptFileHistoryResult.Available(versions);
    }

    // Same per-profile workspace formula the engine uses when it clones — including the
    // configurable workspaces-root override, so the viewer follows relocated clones. Null when the
    // repository has never been synced on this machine.
    private async Task<string?> ResolveWorkspaceAsync(GitRepositoryProfile repository, CancellationToken cancellationToken)
    {
        var overridePath = await _settings.GetWorkspacesRootOverrideAsync(cancellationToken).ConfigureAwait(false);
        var root = string.IsNullOrWhiteSpace(overridePath) ? _defaultWorkspacesRoot : overridePath.Trim();
        var workspace = Path.Combine(root, repository.Id.ToString("N"));
        return Directory.Exists(Path.Combine(workspace, ".git")) ? workspace : null;
    }

    private static string Short(string sha) => sha[..Math.Min(7, sha.Length)];
}
