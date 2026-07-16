using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared.Abstractions;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// A pasted access token often carries a trailing newline; the dialog must store it trimmed, or
/// the Octokit API path (which, unlike the git path, does not trim) sends the newline verbatim.
/// </summary>
public sealed class RepositoryDialogViewModelTests
{
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();

    private RepositoryDialogViewModel NewViewModel() => new(
        Substitute.For<IRepositoryProfileRepository>(),
        Substitute.For<IGitHubService>(),
        _credentials,
        Substitute.For<IClock>(),
        Substitute.For<IAuditWriter>());

    [Fact]
    public async Task Save_StoresTheTokenTrimmed()
    {
        var vm = NewViewModel();
        vm.Name = "Repo";
        vm.Owner = "octo";
        vm.RepositoryName = "scripts";
        vm.Token = "ghp_abc123\n";

        await vm.SaveCommand.ExecuteAsync(null);

        _credentials.Received(1).Store(Arg.Any<string>(), "ghp_abc123");
    }

    [Fact]
    public async Task Save_DoesNotStoreAWhitespaceOnlyToken()
    {
        // Editing with the box "blank" (a stray newline) must keep the saved token, not replace it.
        var vm = NewViewModel();
        vm.Name = "Repo";
        vm.Owner = "octo";
        vm.RepositoryName = "scripts";
        vm.Token = "\n";

        await vm.SaveCommand.ExecuteAsync(null);

        _credentials.DidNotReceive().Store(Arg.Any<string>(), Arg.Any<string>());
    }
}
