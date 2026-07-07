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
    private readonly IAuditWriter _audit;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];

    public RepositoriesViewModel(
        IRepositoryProfileRepository repository, IGitHubService gitHub, ICredentialStore credentialStore,
        IAuditWriter audit)
    {
        _repository = repository;
        _gitHub = gitHub;
        _credentialStore = credentialStore;
        _audit = audit;
    }

    public async Task LoadAsync()
    {
        var repositories = await _repository.GetAllAsync();
        Repositories.Clear();
        foreach (var repository in repositories)
        {
            Repositories.Add(repository);
        }
    }

    /// <summary>Re-runs the token permission check against GitHub using the SAVED token.</summary>
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
                StatusMessage = $"{repository.Name}: no token saved — edit the repository to add one.";
                return;
            }

            var result = await _gitHub.CheckRepositoryAccessAsync(token, repository.Owner, repository.RepositoryName);
            StatusMessage = result.IsSuccess
                ? $"{repository.Name}: {RepositoryDialogViewModel.SummarizeTokenReport(result.Value)}"
                : $"{repository.Name}: {result.Error}";
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
            await _audit.WriteAsync(AuditAction.RepositoryDeleted, "Repository", repository.Id.ToString(), repository.Name);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete {repository.Name} — it may still be used by a sync job. ({ex.Message})";
        }
    }
}
