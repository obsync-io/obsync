using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The Repositories page: lists the GitHub repositories jobs commit to and can re-check their
/// stored tokens or delete them. Adding and editing happen in the Add/Edit Repository dialog
/// (see <see cref="RepositoryDialogViewModel"/>).
/// </summary>
public sealed partial class RepositoriesViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IRepositoryProfileRepository _repository;
    private readonly IGitHubService _gitHub;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly Services.IWorkspaceReclaimer _workspaces;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];

    public RepositoriesViewModel(
        IRepositoryProfileRepository repository, IGitHubService gitHub, ICredentialStore credentialStore,
        IClock clock, IAuditWriter audit, Services.IWorkspaceReclaimer workspaces)
    {
        _repository = repository;
        _gitHub = gitHub;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;
        _workspaces = workspaces;
    }

    public async Task LoadAsync()
    {
        var repositories = await _repository.GetAllAsync();
        var now = _clock.UtcNow;
        Repositories.Clear();
        foreach (var repository in repositories)
        {
            // A validation nobody has re-run in a month stops asserting health — the pill was
            // otherwise permanent, so a token validated in January and expired in March still read
            // as Valid in September, with the age visible only on hover.
            repository.DisplayValidationStatus = repository.EffectiveValidationStatus(now);
            Repositories.Add(repository);
        }
    }

    /// <summary>
    /// Re-runs the validation (token permissions + default-branch existence) against GitHub using
    /// the SAVED token, and persists the outcome so the Status badge survives restarts. A check that
    /// could not run at all (e.g. GitHub unreachable) says nothing about the repository, so the
    /// stored outcome is left untouched.
    /// </summary>
    [RelayCommand]
    private async Task CheckTokenAsync(GitRepositoryProfile? repository)
    {
        if (repository is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Checking the token for {repository.Name}…";
        try
        {
            var token = _credentialStore.Retrieve(CredentialKeys.GitHubToken(repository.Id));
            if (string.IsNullOrEmpty(token))
            {
                // Recorded, not just reported. A repository with no credential at all cannot be
                // valid, and this is the most certain evidence the product ever has — more certain
                // than any answer GitHub could give. It previously returned without touching the
                // stored verdict, so a repository whose token had been removed from Windows
                // Credential Manager went on displaying a green "Valid" badge.
                const string missing = "No token is saved for this repository — edit it to add one.";
                await _repository.UpdateValidationStatusAsync(
                    repository.Id, RepositoryValidationStatus.Failed, _clock.UtcNow, missing);
                StatusMessage = $"{repository.Name}: {missing}";
                await LoadAsync();
                return;
            }

            var result = await RepositoryDialogViewModel.ValidateRepositoryAsync(
                _gitHub, token, repository.Owner, repository.RepositoryName, repository.DefaultBranch);
            if (result.IsFailure)
            {
                // Deliberately does NOT touch the stored verdict: a check that could not run says
                // nothing about the repository, and overwriting a good result because the network
                // blinked would be its own kind of lie. But the row still shows the previous
                // verdict, so the message has to say so — otherwise the page asserts "the check
                // failed" and "this repository is Valid" side by side with nothing to reconcile them.
                StatusMessage =
                    $"{repository.Name}: {result.Error} The status shown is from the last completed check "
                    + "and has not been changed.";
                return;
            }

            var outcome = result.Value;
            await _repository.UpdateValidationStatusAsync(repository.Id, outcome.Status, _clock.UtcNow, outcome.Detail);
            StatusMessage = outcome.BranchWarning is null
                ? $"{repository.Name}: {outcome.Detail}"
                : $"{repository.Name}: {outcome.Detail} {outcome.BranchWarning}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{repository.Name}: token check failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(GitRepositoryProfile? repository)
    {
        if (repository is null)
        {
            return;
        }

        try
        {
            // Delete the row FIRST so an FK restriction (a sync job still using this repository)
            // surfaces before the token is removed — otherwise the token would be orphaned.
            await _repository.DeleteAsync(repository.Id);
            _credentialStore.Delete(CredentialKeys.GitHubToken(repository.Id));

            // Reclaim the clone. The workspace path is keyed on this profile's id and nothing else,
            // so once the row is gone the directory — potentially many gigabytes of a schema estate
            // — has no remaining reference in the product and nothing could ever find it again.
            // Best-effort by design: everything in a workspace is regenerable, so failing to delete
            // it must never fail the delete the user actually asked for.
            var reclaimed = await _workspaces.ReclaimForRepositoryAsync(repository.Id);

            await _audit.WriteAsync(AuditAction.RepositoryDeleted, "Repository", repository.Id.ToString(), repository.Name);
            await LoadAsync();
            StatusMessage = reclaimed > 0
                ? $"Deleted {repository.Name} and reclaimed {FormatBytes(reclaimed)} of workspace."
                : $"Deleted {repository.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete {repository.Name} — it may still be used by a sync job. ({ex.Message})";
        }
    }

    /// <summary>Human-readable size for the reclaim message; whole units, no false precision.</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} bytes",
    };
}
