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

/// <summary>
/// Drives the Add / Edit Repository dialog: the GitHub coordinates, the token permission checker,
/// and a Save that stores the profile and its access token.
/// </summary>
public sealed partial class RepositoryDialogViewModel : ObservableObject
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
    [ObservableProperty] private bool _isEditMode;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The token permission checklist shown after "Validate".</summary>
    public ObservableCollection<PermissionCheckLine> PermissionChecks { get; } = [];

    public event EventHandler? Saved;

    public RepositoryDialogViewModel(
        IRepositoryProfileRepository repository, IGitHubService gitHub, ICredentialStore credentialStore, IClock clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _gitHub = gitHub;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;
    }

    public string Title => IsEditMode ? "Edit repository" : "Add a repository";
    public string TokenHint => IsEditMode ? "ACCESS TOKEN (LEAVE BLANK TO KEEP THE SAVED ONE)" : "FINE-GRAINED ACCESS TOKEN";

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TokenHint));
    }

    /// <summary>Populates the dialog from an existing repository for editing.</summary>
    public void LoadForEdit(GitRepositoryProfile repository)
    {
        IsEditMode = true;
        _editingId = repository.Id;
        _editingCreatedAt = repository.CreatedAt;
        _editingRemoteUrl = repository.RemoteUrl;
        Name = repository.Name;
        Owner = repository.Owner;
        RepositoryName = repository.RepositoryName;
        DefaultBranch = repository.DefaultBranch;
        Token = string.Empty;
        ValidationResult = null;
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        if (string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(RepositoryName))
        {
            ValidationResult = "Enter the owner and repository name first, then validate.";
            return;
        }

        var token = ResolveToken();
        if (string.IsNullOrEmpty(token))
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
            var result = await _gitHub.CheckRepositoryAccessAsync(token, Owner.Trim(), RepositoryName.Trim());
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

            ValidationResult = SummarizeTokenReport(report);
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
                IsEditMode ? AuditAction.RepositoryEdited : AuditAction.RepositoryAdded, "Repository", profile.Id.ToString(), profile.Name);

            Saved?.Invoke(this, EventArgs.Empty);
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

    // The typed token wins; when editing with the box left blank, fall back to the saved one so a
    // stored token can be re-checked without re-pasting it.
    private string? ResolveToken()
    {
        if (!string.IsNullOrEmpty(Token))
        {
            return Token;
        }

        return _editingId is { } id ? _credentialStore.Retrieve(CredentialKeys.GitHubToken(id)) : null;
    }

    /// <summary>
    /// One-line verdict for a token permission report. Calls out the write gap explicitly: a
    /// read-only token validates fine but silently fails every push — the exact failure this
    /// checker exists to catch. Shared with the Repositories page's row-level "Check token".
    /// </summary>
    internal static string SummarizeTokenReport(TokenPermissionReport report) => report switch
    {
        { TokenValid: false } => report.Detail ?? "The token is invalid.",
        { RepositoryFound: false } => report.Detail ?? "The repository could not be accessed.",
        { CanWrite: false } => "The token can read but NOT write. Pushes will fail — grant it Contents: write.",
        _ => $"All checks passed — authenticated as {report.Login}.",
    };
}
