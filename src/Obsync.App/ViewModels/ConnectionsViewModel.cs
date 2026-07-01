using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Manages reusable SQL Server connection profiles and their stored passwords.</summary>
public sealed partial class ConnectionsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IConnectionProfileRepository _repository;
    private readonly ISqlServerProbe _probe;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;

    private Guid? _editingId;
    private DateTimeOffset _editingCreatedAt;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private SqlAuthenticationMode _authenticationMode = SqlAuthenticationMode.WindowsIntegrated;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEditing;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string Password { get; set; } = string.Empty;

    public ObservableCollection<SqlConnectionProfile> Connections { get; } = [];
    public IReadOnlyList<SqlAuthenticationMode> AuthModes { get; } =
        [SqlAuthenticationMode.WindowsIntegrated, SqlAuthenticationMode.SqlLogin];

    /// <summary>Raised when the view should clear its PasswordBox (after a save, edit, or cancel).</summary>
    public event EventHandler? SecretInputShouldClear;

    public ConnectionsViewModel(
        IConnectionProfileRepository repository, ISqlServerProbe probe, ICredentialStore credentialStore, IClock clock)
    {
        _repository = repository;
        _probe = probe;
        _credentialStore = credentialStore;
        _clock = clock;
    }

    public string EditorTitle => IsEditing ? "Edit connection" : "Add a connection";
    public string PasswordHint => IsEditing ? "Password (leave blank to keep the saved one)" : "Password (SQL login)";

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PasswordHint));
    }

    public async Task LoadAsync()
    {
        var connections = await _repository.GetAllAsync();
        Connections.Clear();
        foreach (var connection in connections)
        {
            Connections.Add(connection);
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        TestResult = "Testing…";
        try
        {
            var profile = BuildProfile();
            var result = await _probe.TestConnectionAsync(profile, ResolveTestPassword());
            TestResult = result.IsSuccess
                ? $"Connected — {result.Value.Edition} ({result.Value.ProductVersion})."
                : result.Error;
        }
        catch (Exception ex)
        {
            TestResult = $"Test failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The password to test with: the typed value if present, otherwise (when editing) the secret
    /// already stored for this profile — so testing an existing SQL login without retyping works.
    /// </summary>
    private string? ResolveTestPassword()
    {
        if (AuthenticationMode != SqlAuthenticationMode.SqlLogin)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(Password))
        {
            return Password;
        }

        return _editingId is { } id ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(id)) : Password;
    }

    [RelayCommand]
    private void Edit(SqlConnectionProfile? connection)
    {
        if (connection is null)
        {
            return;
        }

        _editingId = connection.Id;
        _editingCreatedAt = connection.CreatedAt;
        Name = connection.Name;
        ServerName = connection.ServerName;
        AuthenticationMode = connection.AuthenticationMode;
        Username = connection.Username ?? string.Empty;
        TrustServerCertificate = connection.TrustServerCertificate;
        Password = string.Empty;
        IsEditing = true;
        TestResult = null;
        SecretInputShouldClear?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEdit() => ResetEditor();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(ServerName))
        {
            TestResult = "Name and server are required.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var profile = BuildProfile();
            profile.Id = _editingId ?? profile.Id;
            profile.CreatedAt = _editingId is null ? _clock.UtcNow : _editingCreatedAt;
            profile.UpdatedAt = _clock.UtcNow;
            await _repository.UpsertAsync(profile);

            if (AuthenticationMode == SqlAuthenticationMode.SqlLogin)
            {
                if (!string.IsNullOrEmpty(Password))
                {
                    _credentialStore.Store(CredentialKeys.SqlPassword(profile.Id), Password);
                }
            }
            else
            {
                // Switched away from SQL login — drop any stored password so none is left orphaned.
                _credentialStore.Delete(CredentialKeys.SqlPassword(profile.Id));
            }

            TestResult = "Saved.";
            ResetEditor();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            TestResult = $"Could not save — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SqlConnectionProfile? connection)
    {
        if (connection is null)
        {
            return;
        }

        _credentialStore.Delete(CredentialKeys.SqlPassword(connection.Id));
        await _repository.DeleteAsync(connection.Id);
        if (_editingId == connection.Id)
        {
            ResetEditor();
        }

        await LoadAsync();
    }

    private void ResetEditor()
    {
        _editingId = null;
        _editingCreatedAt = default;
        Name = string.Empty;
        ServerName = string.Empty;
        AuthenticationMode = SqlAuthenticationMode.WindowsIntegrated;
        Username = string.Empty;
        TrustServerCertificate = true;
        Password = string.Empty;
        IsEditing = false;
        SecretInputShouldClear?.Invoke(this, EventArgs.Empty);
    }

    private SqlConnectionProfile BuildProfile() => new()
    {
        Name = Name.Trim(),
        ServerName = ServerName.Trim(),
        AuthenticationMode = AuthenticationMode,
        Username = AuthenticationMode == SqlAuthenticationMode.SqlLogin ? Username.Trim() : null,
        TrustServerCertificate = TrustServerCertificate,
    };
}
