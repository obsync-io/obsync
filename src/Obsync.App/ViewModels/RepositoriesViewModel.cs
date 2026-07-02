using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>One line of the token permission checklist, e.g. "Write / push — OK".</summary>
public sealed record PermissionCheckLine(string Label, bool Ok);

/// <summary>Manages reusable GitHub repository profiles and their stored access tokens.</summary>
public sealed partial class RepositoriesViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IRepositoryProfileRepository _repository;
    private readonly IGitHubService _gitHub;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

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

    /// <summary>The token permission checklist shown after "Validate".</summary>
    public ObservableCollection<PermissionCheckLine> PermissionChecks { get; } = [];

    /// <summary>Raised when the view should clear its token PasswordBox (after a save, edit, or cancel).</summary>
    public event EventHandler? SecretInputShouldClear;

    public RepositoriesViewModel(
        IRepositoryProfileRepository repository, IGitHubService gitHub, ICredentialStore credentialStore, IClock clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _gitHub = gitHub;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;
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
        if (string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(RepositoryName))
        {
            ValidationResult = "Enter the owner and repository name first, then validate.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            ValidationResult = "Enter a token to validate.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        PermissionChecks.Clear();
        ValidationResult = "Checking token permissions…";
        try
        {
            var result = await _gitHub.CheckRepositoryAccessAsync(Token, Owner.Trim(), RepositoryName.Trim());
            if (result.IsFailure)
            {
                ValidationResult = result.Error;
                return;
            }

            var report = result.Value;
            PermissionChecks.Add(new PermissionCheckLine("Token valid", report.TokenValid));
            PermissionChecks.Add(new PermissionCheckLine($"Repository access — {Owner.Trim()}/{RepositoryName.Trim()}", report.RepositoryFound));
            PermissionChecks.Add(new PermissionCheckLine("Read (pull)", report.CanRead));
            PermissionChecks.Add(new PermissionCheckLine("Write / push — Contents", report.CanWrite));

            // The summary calls out the write gap explicitly: a read-only token validates fine but
            // silently fails every push — the exact failure this checker exists to catch.
            ValidationResult = report switch
            {
                { TokenValid: false } => report.Detail ?? "The token is invalid.",
                { RepositoryFound: false } => report.Detail ?? "The repository could not be accessed.",
                { CanWrite: false } => "The token can read but NOT write. Pushes will fail — grant it Contents: write.",
                _ => $"All checks passed — authenticated as {report.Login}.",
            };
        }
        catch (Exception ex)
        {
            ValidationResult = $"Validation failed — {ex.Message}";
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
        PermissionChecks.Clear();
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

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var isEdit = _editingId is not null;
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

            await _audit.WriteAsync(
                isEdit ? AuditAction.RepositoryEdited : AuditAction.RepositoryAdded, "Repository", profile.Id.ToString(), profile.Name);

            ValidationResult = "Saved.";
            ResetEditor();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ValidationResult = $"Could not save — {ex.Message}";
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
            if (_editingId == repository.Id)
            {
                ResetEditor();
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            ValidationResult = $"Could not delete {repository.Name} — it may still be used by a sync job. ({ex.Message})";
        }
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
        PermissionChecks.Clear();
        SecretInputShouldClear?.Invoke(this, EventArgs.Empty);
    }
}
