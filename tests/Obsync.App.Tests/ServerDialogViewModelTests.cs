using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The Add Server dialog must not weaken TLS by default: "Trust server certificate" starts OFF
/// (matching the model default) and only an explicit choice — or the stored profile — turns it on.
/// </summary>
public sealed class ServerDialogViewModelTests
{
    private static ServerDialogViewModel NewViewModel() => new(
        Substitute.For<IConnectionProfileRepository>(),
        Substitute.For<ISqlServerProbe>(),
        Substitute.For<ICredentialStore>(),
        Substitute.For<IClock>(),
        Substitute.For<IAuditWriter>());

    [Fact]
    public void NewServer_DefaultsToNotTrustingTheServerCertificate()
    {
        var vm = NewViewModel();

        Assert.False(vm.TrustServerCertificate);
        Assert.True(vm.Encrypt);
    }

    [Fact]
    public void EditingAServer_LoadsItsStoredTrustSetting()
    {
        var vm = NewViewModel();

        vm.LoadForEdit(new SqlConnectionProfile
        {
            Name = "Legacy",
            ServerName = "SQL01",
            TrustServerCertificate = true,
        });

        Assert.True(vm.TrustServerCertificate);
    }
}
