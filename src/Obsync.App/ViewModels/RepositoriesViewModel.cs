using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Manages reusable GitHub repository profiles and their stored access tokens.</summary>
public sealed partial class RepositoriesViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IRepositoryProfileRepository _repository;
    private readonly IGitHubService _gitHub;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _owner = string.Empty;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string _defaultBranch = "main";
    [ObservableProperty] private string? _validationResult;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string Token { get; set; } = string.Empty;

    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];

    public RepositoriesViewModel(
        IRepositoryProfileRepository repository, IGitHubService gitHub, ICredentialStore credentialStore, IClock clock)
    {
        _repository = repository;
        _gitHub = gitHub;
        _credentialStore = credentialStore;
        _clock = clock;
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

    [RelayCommand]
    private async Task ValidateAsync()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            ValidationResult = "Enter a token to validate.";
            return;
        }

        IsBusy = true;
        ValidationResult = "Validating…";
        try
        {
            var result = await _gitHub.ValidateTokenAsync(Token);
            ValidationResult = result.IsSuccess ? $"Valid — authenticated as {result.Value}." : result.Error;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(RepositoryName))
        {
            ValidationResult = "Name, owner, and repository are required.";
            return;
        }

        var profile = new GitRepositoryProfile
        {
            Name = Name.Trim(),
            Owner = Owner.Trim(),
            RepositoryName = RepositoryName.Trim(),
            DefaultBranch = string.IsNullOrWhiteSpace(DefaultBranch) ? "main" : DefaultBranch.Trim(),
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        await _repository.UpsertAsync(profile);

        if (!string.IsNullOrEmpty(Token))
        {
            _credentialStore.Store(CredentialKeys.GitHubToken(profile.Id), Token);
        }

        ValidationResult = "Saved.";
        Token = string.Empty;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(GitRepositoryProfile? repository)
    {
        if (repository is null)
        {
            return;
        }

        _credentialStore.Delete(CredentialKeys.GitHubToken(repository.Id));
        await _repository.DeleteAsync(repository.Id);
        await LoadAsync();
    }
}
