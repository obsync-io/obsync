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

/// <summary>
/// Reads a scripted object's before/after content for a commit from the repository's LOCAL git
/// workspace (the same clone the sync engine commits from). Never touches the network: when the
/// workspace or the commit is missing locally it reports "unavailable" with a reason instead.
/// </summary>
public interface IScriptHistoryService
{
    Task<ScriptVersionsResult> GetVersionsAsync(
        GitRepositoryProfile repository, string commitSha, string relativePath, ChangeType changeType,
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

    public async Task<ScriptVersionsResult> GetVersionsAsync(
        GitRepositoryProfile repository, string commitSha, string relativePath, ChangeType changeType,
        CancellationToken cancellationToken = default)
    {
        // Same per-profile workspace formula the engine uses when it clones — including the
        // configurable workspaces-root override, so the viewer follows relocated clones.
        var overridePath = await _settings.GetWorkspacesRootOverrideAsync(cancellationToken).ConfigureAwait(false);
        var root = string.IsNullOrWhiteSpace(overridePath) ? _defaultWorkspacesRoot : overridePath.Trim();
        var workspace = Path.Combine(root, repository.Id.ToString("N"));
        if (!Directory.Exists(Path.Combine(workspace, ".git")))
        {
            return ScriptVersionsResult.Unavailable(
                "This repository hasn't been synced on this machine yet, so there is no local copy of the scripts.");
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

    private static string Short(string sha) => sha[..Math.Min(7, sha.Length)];
}
