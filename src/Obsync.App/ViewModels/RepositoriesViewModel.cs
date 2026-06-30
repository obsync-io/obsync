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

    private Guid? _editingId;
    private DateTimeOffset _editingCreatedAt;
    private string? _editingRemoteUrl;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _owner = string.Empty;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string _defaultBranch = "main";
    [ObservableProperty] private string? _validationResult;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEditing;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string Token { get; set; } = string.Empty;

    public ObservableCollection<GitRepositoryProfile> Repositories { get; } = [];

    /// <summary>Raised when the view should clear its token PasswordBox (after a save, edit, or cancel).</summary>
    public event EventHandler? SecretInputShouldClear;

    public RepositoriesViewModel(
        IRepositoryProfileRepository repository, IGitHubService gitHub, ICredentialStore credentialStore, IClock clock)
    {
        _repository = repository;
        _gitHub = gitHub;
        _credentialStore = credentialStore;
        _clock = clock;
    }

    public string EditorTitle => IsEditing ? "Edit repository" : "Add a repository";
    public string TokenHint => IsEditing ? "Access token (leave blank to keep the saved one)" : "Fine-grained access token";

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(TokenHint));
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
    private void Edit(GitRepositoryProfile? repository)
    {
        if (repository is null)
        {
            return;
        }

        _editingId = repository.Id;
        _editingCreatedAt = repository.CreatedAt;
        _editingRemoteUrl = repository.RemoteUrl;
        Name = repository.Name;
        Owner = repository.Owner;
        RepositoryName = repository.RepositoryName;
        DefaultBranch = repository.DefaultBranch;
        Token = string.Empty;
        IsEditing = true;
        ValidationResult = null;
        SecretInputShouldClear?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEdit() => ResetEditor();

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
            Id = _editingId ?? Guid.NewGuid(),
            Name = Name.Trim(),
            Owner = Owner.Trim(),
            RepositoryName = RepositoryName.Trim(),
            RemoteUrl = _editingRemoteUrl,
            DefaultBranch = string.IsNullOrWhiteSpace(DefaultBranch) ? "main" : DefaultBranch.Trim(),
            CreatedAt = _editingId is null ? _clock.UtcNow : _editingCreatedAt,
            UpdatedAt = _clock.UtcNow,
        };
        await _repository.UpsertAsync(profile);

        if (!string.IsNullOrEmpty(Token))
        {
            _credentialStore.Store(CredentialKeys.GitHubToken(profile.Id), Token);
        }

        ValidationResult = "Saved.";
        ResetEditor();
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
        if (_editingId == repository.Id)
        {
            ResetEditor();
        }

        await LoadAsync();
    }

    private void ResetEditor()
    {
        _editingId = null;
        _editingCreatedAt = default;
        _editingRemoteUrl = null;
        Name = string.Empty;
        Owner = string.Empty;
        RepositoryName = string.Empty;
        DefaultBranch = "main";
        Token = string.Empty;
        IsEditing = false;
        SecretInputShouldClear?.Invoke(this, EventArgs.Empty);
    }
}
