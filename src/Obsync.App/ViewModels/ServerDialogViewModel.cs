using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// Drives the Add / Edit Server dialog: the connection fields, a live "Test connection", and a
/// Save that tests the connection and records the outcome with the server.
/// </summary>
public sealed partial class ServerDialogViewModel : ObservableObject
{
    private readonly IConnectionProfileRepository _repository;
    private readonly ISqlServerProbe _probe;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

    private Guid? _editingId;
    private DateTimeOffset _editingCreatedAt;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private SqlAuthenticationMode _authenticationMode = SqlAuthenticationMode.WindowsIntegrated;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _encrypt = true;

    // Off by default (matches the model): trusting any certificate silently disables TLS
    // validation. The edit path still loads whatever the stored profile says.
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private int _connectTimeoutSeconds = 30;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEditMode;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string Password { get; set; } = string.Empty;

    public IReadOnlyList<SqlAuthenticationMode> AuthModes { get; } =
        [SqlAuthenticationMode.WindowsIntegrated, SqlAuthenticationMode.SqlLogin];

    /// <summary>The current Windows identity, shown (read-only) when Windows authentication is selected.</summary>
    public string WindowsIdentityName { get; } = ResolveWindowsIdentity();

    public event EventHandler? Saved;

    public ServerDialogViewModel(
        IConnectionProfileRepository repository, ISqlServerProbe probe, ICredentialStore credentialStore, IClock clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _probe = probe;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;
    }

    public string Title => IsEditMode ? "Edit server" : "Add a server";
    public bool IsSqlLogin => AuthenticationMode == SqlAuthenticationMode.SqlLogin;
    public bool IsWindowsAuth => AuthenticationMode == SqlAuthenticationMode.WindowsIntegrated;
    public string PasswordHint => IsEditMode ? "Password (leave blank to keep the saved one)" : "Password";

    partial void OnIsEditModeChanged(bool value) => OnPropertyChanged(nameof(Title));

    partial void OnAuthenticationModeChanged(SqlAuthenticationMode value)
    {
        OnPropertyChanged(nameof(IsSqlLogin));
        OnPropertyChanged(nameof(IsWindowsAuth));
    }

    /// <summary>Populates the dialog from an existing server for editing.</summary>
    public void LoadForEdit(SqlConnectionProfile server)
    {
        IsEditMode = true;
        _editingId = server.Id;
        _editingCreatedAt = server.CreatedAt;
        Name = server.Name;
        ServerName = server.ServerName;
        AuthenticationMode = server.AuthenticationMode;
        Username = server.Username ?? string.Empty;
        Encrypt = server.Encrypt;
        TrustServerCertificate = server.TrustServerCertificate;
        ConnectTimeoutSeconds = server.ConnectTimeoutSeconds;
        Password = string.Empty;
        TestResult = null;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        TestSucceeded = false;
        TestResult = "Testing…";
        try
        {
            var result = await _probe.TestConnectionAsync(BuildProfile(), ResolveTestPassword());
            TestSucceeded = result.IsSuccess;
            TestResult = result.IsSuccess
                ? $"Connected — SQL Server {result.Value.Edition} ({result.Value.ProductVersion})."
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

            // Test the connection immediately and record the outcome with the server.
            var test = await _probe.TestConnectionAsync(profile, ResolveTestPassword());
            profile.LastTestStatus = test.IsSuccess ? ConnectionTestStatus.Connected : ConnectionTestStatus.Failed;
            profile.LastTestedAt = _clock.UtcNow;
            profile.LastTestDetail = test.IsSuccess
                ? $"SQL Server {test.Value.Edition} ({test.Value.ProductVersion})"
                : test.Error;

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
                _credentialStore.Delete(CredentialKeys.SqlPassword(profile.Id));
            }

            await _audit.WriteAsync(
                IsEditMode ? AuditAction.ServerEdited : AuditAction.ServerAdded, "Server", profile.Id.ToString(), profile.Name);

            Saved?.Invoke(this, EventArgs.Empty);
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

    private SqlConnectionProfile BuildProfile() => new()
    {
        Name = Name.Trim(),
        ServerName = ServerName.Trim(),
        AuthenticationMode = AuthenticationMode,
        Username = AuthenticationMode == SqlAuthenticationMode.SqlLogin ? Username.Trim() : null,
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
        ConnectTimeoutSeconds = ConnectTimeoutSeconds <= 0 ? 30 : ConnectTimeoutSeconds,
    };

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

    private static string ResolveWindowsIdentity()
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }
}
