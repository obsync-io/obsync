using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;

namespace Obsync.App.ViewModels;

/// <summary>One line of the token permission checklist, e.g. "Write / push — OK".</summary>
public sealed record PermissionCheckLine(string Label, bool Ok);

/// <summary>
/// The computed outcome of a full repository validation: the token permission report, whether the
/// default branch exists (null when it could not be checked), a warning when the branch listing
/// itself failed, and the status + detail persisted with the profile.
/// </summary>
internal sealed record RepositoryValidationOutcome(
    TokenPermissionReport Report, bool? BranchExists, string? BranchWarning,
    RepositoryValidationStatus Status, string Detail);

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
    private (string Owner, string Repo, string Branch)? _loadedCoordinates;
    private (RepositoryValidationStatus Status, DateTimeOffset? At, string? Detail) _loadedValidation;
    private RepositoryValidationOutcome? _pendingValidation;
    private DateTimeOffset _pendingValidatedAt;
    private (string Owner, string Repo, string Branch)? _pendingValidationCoordinates;

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
        _loadedCoordinates = (repository.Owner, repository.RepositoryName, repository.DefaultBranch);
        _loadedValidation = (repository.LastValidationStatus, repository.LastValidatedAt, repository.LastValidationDetail);
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
            var owner = Owner.Trim();
            var repositoryName = RepositoryName.Trim();
            var branch = EffectiveBranch;
            var result = await ValidateRepositoryAsync(_gitHub, token, owner, repositoryName, branch);
            if (result.IsFailure)
            {
                ValidationResult = result.Error;
                return;
            }

            var outcome = result.Value;
            var report = outcome.Report;
            PermissionChecks.Add(new PermissionCheckLine("Token valid", report.TokenValid));
            PermissionChecks.Add(new PermissionCheckLine($"Repository access — {owner}/{repositoryName}", report.RepositoryFound));
            PermissionChecks.Add(new PermissionCheckLine("Read (pull)", report.CanRead));
            PermissionChecks.Add(new PermissionCheckLine("Write / push — Contents", report.CanWrite));
            if (outcome.BranchExists is { } branchExists)
            {
                PermissionChecks.Add(new PermissionCheckLine($"Branch '{branch}' exists", branchExists));
            }

            ValidationResult = outcome.BranchWarning is null ? outcome.Detail : $"{outcome.Detail} {outcome.BranchWarning}";

            // Remember the outcome so Save persists it, and record it immediately when re-validating
            // a saved repository whose coordinates are unchanged — mirroring the Repositories page's
            // row-level "Check token". Changed-but-unsaved coordinates must not overwrite the row.
            _pendingValidation = outcome;
            _pendingValidatedAt = _clock.UtcNow;
            _pendingValidationCoordinates = (owner, repositoryName, branch);
            if (_editingId is { } id && _pendingValidationCoordinates == _loadedCoordinates)
            {
                await _repository.UpdateValidationStatusAsync(id, outcome.Status, _pendingValidatedAt, outcome.Detail);
            }
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
            var owner = Owner.Trim();
            var repositoryName = RepositoryName.Trim();
            var branch = EffectiveBranch;
            var validation = ResolveValidationToPersist(owner, repositoryName, branch);
            var profile = new GitRepositoryProfile
            {
                Id = _editingId ?? Guid.NewGuid(),
                Name = Name.Trim(),
                Owner = owner,
                RepositoryName = repositoryName,
                RemoteUrl = _editingRemoteUrl,
                DefaultBranch = branch,
                LastValidationStatus = validation.Status,
                LastValidatedAt = validation.At,
                LastValidationDetail = validation.Detail,
                CreatedAt = _editingId is null ? _clock.UtcNow : _editingCreatedAt,
                UpdatedAt = _clock.UtcNow,
            };
            await _repository.UpsertAsync(profile);

            // Trimmed: a pasted token often carries a trailing newline, which the Octokit path
            // sends verbatim (the git path already trims).
            if (Token.Trim() is { Length: > 0 } token)
            {
                _credentialStore.Store(CredentialKeys.GitHubToken(profile.Id), token);
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

    // The typed token wins (trimmed — pasted tokens carry trailing newlines); when editing with the
    // box left blank, fall back to the saved one so a stored token can be re-checked without
    // re-pasting it.
    private string? ResolveToken()
    {
        if (Token.Trim() is { Length: > 0 } token)
        {
            return token;
        }

        return _editingId is { } id ? _credentialStore.Retrieve(CredentialKeys.GitHubToken(id)) : null;
    }

    /// <summary>The branch a validation and a save both act on; defaults to "main" like the model.</summary>
    private string EffectiveBranch => string.IsNullOrWhiteSpace(DefaultBranch) ? "main" : DefaultBranch.Trim();

    // The persisted validation must describe the coordinates being saved: a validation run in this
    // dialog wins when it matched them; otherwise an unchanged edit keeps its stored outcome, and
    // changed coordinates reset to Unvalidated (the old verdict no longer applies).
    private (RepositoryValidationStatus Status, DateTimeOffset? At, string? Detail) ResolveValidationToPersist(
        string owner, string repositoryName, string branch)
    {
        if (_pendingValidation is { } outcome && _pendingValidationCoordinates == (owner, repositoryName, branch))
        {
            return (outcome.Status, _pendingValidatedAt, outcome.Detail);
        }

        if (IsEditMode && _loadedCoordinates == (owner, repositoryName, branch))
        {
            return _loadedValidation;
        }

        return (RepositoryValidationStatus.Unvalidated, null, null);
    }

    /// <summary>
    /// Runs the full validation shared by this dialog's Validate and the Repositories page's
    /// row-level "Check token": the token permission report, then — when the repository is
    /// reachable — a case-sensitive check that <paramref name="branch"/> exists. A branch-listing
    /// API failure is reported as a warning, never a failure. A failed <see cref="Result"/> means
    /// the check itself could not run (e.g. GitHub unreachable).
    /// </summary>
    internal static async Task<Result<RepositoryValidationOutcome>> ValidateRepositoryAsync(
        IGitHubService gitHub, string token, string owner, string repositoryName, string branch)
    {
        var access = await gitHub.CheckRepositoryAccessAsync(token, owner, repositoryName);
        if (access.IsFailure)
        {
            // A failed result always carries an error message (enforced by Result's constructor).
            return Result.Failure<RepositoryValidationOutcome>(access.Error!);
        }

        var report = access.Value;
        var status = report switch
        {
            { TokenValid: false } or { RepositoryFound: false } => RepositoryValidationStatus.Failed,
            { CanWrite: false } => RepositoryValidationStatus.Attention,
            _ => RepositoryValidationStatus.Valid,
        };
        var detail = SummarizeTokenReport(report);

        bool? branchExists = null;
        string? branchWarning = null;
        if (report.RepositoryFound)
        {
            var branches = await gitHub.GetBranchesAsync(token, owner, repositoryName);
            if (branches.IsFailure)
            {
                branchWarning = $"Could not verify that branch '{branch}' exists — {branches.Error}";
            }
            else
            {
                branchExists = branches.Value.Contains(branch, StringComparer.Ordinal);
                if (!branchExists.Value)
                {
                    status = RepositoryValidationStatus.Failed;
                    detail = $"Branch '{branch}' not found in {owner}/{repositoryName}.";
                }
            }
        }

        return Result.Success(new RepositoryValidationOutcome(report, branchExists, branchWarning, status, detail));
    }

    /// <summary>
    /// One-line verdict for a token permission report, mode-aware: a read-only token is fatal only
    /// for the push-based commit modes (a read-only token used to validate as "OK" and then silently
    /// fail every push — the exact failure this checker exists to catch). Shared with the
    /// Repositories page's row-level "Check token".
    /// </summary>
    internal static string SummarizeTokenReport(TokenPermissionReport report) => report switch
    {
        { TokenValid: false } => report.Detail ?? "The token is invalid.",
        { RepositoryFound: false } => report.Detail ?? "The repository could not be accessed.",
        { CanWrite: false } => "Read-only access — Direct Commit and Pull Request jobs will fail to push; "
            + "Local Commit Only and Export Only are unaffected.",
        _ => $"Read and write access verified — authenticated as {report.Login}.",
    };
}
