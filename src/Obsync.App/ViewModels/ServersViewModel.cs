using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// The Servers page: lists the SQL Server connections Obsync uses and can re-test or delete them.
/// Adding and editing happen in the Add/Edit Server dialog (see <see cref="ServerDialogViewModel"/>).
/// </summary>
public sealed partial class ServersViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IConnectionProfileRepository _repository;
    private readonly ISqlServerProbe _probe;
    private readonly ICredentialStore _credentialStore;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<SqlConnectionProfile> Servers { get; } = [];

    public ServersViewModel(
        IConnectionProfileRepository repository, ISqlServerProbe probe, ICredentialStore credentialStore, IClock clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _probe = probe;
        _credentialStore = credentialStore;
        _clock = clock;
        _audit = audit;
    }

    public async Task LoadAsync()
    {
        var servers = await _repository.GetAllAsync();
        Servers.Clear();
        foreach (var server in servers)
        {
            Servers.Add(server);
        }
    }

    [RelayCommand]
    private async Task TestAsync(SqlConnectionProfile? server)
    {
        if (server is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Testing {server.Name}…";
        try
        {
            var password = server.RequiresPassword
                ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(server.Id))
                : null;
            var result = await _probe.TestConnectionAsync(server, password);
            var status = result.IsSuccess ? ConnectionTestStatus.Connected : ConnectionTestStatus.Failed;
            var detail = result.IsSuccess
                ? $"SQL Server {result.Value.Edition} ({result.Value.ProductVersion})"
                : result.Error;

            // A failed test says nothing about the server's edition/version — keep the last known values.
            var edition = result.IsSuccess ? result.Value.Edition : server.ServerEdition;
            var version = result.IsSuccess ? result.Value.ProductVersion : server.ServerVersion;
            await _repository.UpdateTestStatusAsync(server.Id, status, _clock.UtcNow, detail, edition, version);
            StatusMessage = result.IsSuccess ? $"{server.Name}: connected." : $"{server.Name}: {result.Error}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{server.Name}: test failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SqlConnectionProfile? server)
    {
        if (server is null)
        {
            return;
        }

        try
        {
            // Delete the row FIRST: if a sync job still references this server the FK restriction
            // throws here, before we touch the credential — so the saved password is never orphaned.
            await _repository.DeleteAsync(server.Id);
            _credentialStore.Delete(CredentialKeys.SqlPassword(server.Id));
            await _audit.WriteAsync(AuditAction.ServerDeleted, "Server", server.Id.ToString(), server.Name);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // e.g. a foreign-key restriction when a sync job still references this server.
            StatusMessage = $"Could not delete {server.Name} — it may still be used by a sync job. ({ex.Message})";
        }
    }
}
