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

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private SqlAuthenticationMode _authenticationMode = SqlAuthenticationMode.WindowsIntegrated;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set from the view's PasswordBox; never bound directly.</summary>
    public string Password { get; set; } = string.Empty;

    public ObservableCollection<SqlConnectionProfile> Connections { get; } = [];
    public IReadOnlyList<SqlAuthenticationMode> AuthModes { get; } =
        [SqlAuthenticationMode.WindowsIntegrated, SqlAuthenticationMode.SqlLogin];

    public ConnectionsViewModel(
        IConnectionProfileRepository repository, ISqlServerProbe probe, ICredentialStore credentialStore, IClock clock)
    {
        _repository = repository;
        _probe = probe;
        _credentialStore = credentialStore;
        _clock = clock;
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
        IsBusy = true;
        TestResult = "Testing…";
        try
        {
            var profile = BuildProfile();
            var password = AuthenticationMode == SqlAuthenticationMode.SqlLogin ? Password : null;
            var result = await _probe.TestConnectionAsync(profile, password);
            TestResult = result.IsSuccess
                ? $"Connected — {result.Value.Edition} ({result.Value.ProductVersion})."
                : result.Error;
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

        var profile = BuildProfile();
        profile.CreatedAt = _clock.UtcNow;
        profile.UpdatedAt = _clock.UtcNow;
        await _repository.UpsertAsync(profile);

        if (AuthenticationMode == SqlAuthenticationMode.SqlLogin && !string.IsNullOrEmpty(Password))
        {
            _credentialStore.Store(CredentialKeys.SqlPassword(profile.Id), Password);
        }

        TestResult = "Saved.";
        Password = string.Empty;
        await LoadAsync();
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
        await LoadAsync();
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
