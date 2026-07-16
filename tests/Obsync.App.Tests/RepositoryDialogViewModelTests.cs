using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// A pasted access token often carries a trailing newline; the dialog must store it trimmed, or
/// the Octokit API path (which, unlike the git path, does not trim) sends the newline verbatim.
/// Also covers how Save persists (or honestly resets) the validation outcome.
/// </summary>
public sealed class RepositoryDialogViewModelTests
{
    private readonly IRepositoryProfileRepository _repositories = Substitute.For<IRepositoryProfileRepository>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();

    private RepositoryDialogViewModel NewViewModel(IGitHubService? gitHub = null) => new(
        _repositories,
        gitHub ?? Substitute.For<IGitHubService>(),
        _credentials,
        Substitute.For<IClock>(),
        Substitute.For<IAuditWriter>());

    private static IGitHubService FullAccessGitHub()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: true, Detail: null)));
        gitHub.GetBranchesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>(["main"]));
        return gitHub;
    }

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

    [Fact]
    public async Task Save_AfterValidate_PersistsTheValidationOutcome()
    {
        var vm = NewViewModel(FullAccessGitHub());
        vm.Name = "Repo";
        vm.Owner = "octo";
        vm.RepositoryName = "scripts";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        await _repositories.Received(1).UpsertAsync(
            Arg.Is<GitRepositoryProfile>(p =>
                p.LastValidationStatus == RepositoryValidationStatus.Valid
                && p.LastValidatedAt != null
                && p.LastValidationDetail!.Contains("Read and write access verified")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_WithCoordinatesChangedAfterValidate_ResetsToUnvalidated()
    {
        var vm = NewViewModel(FullAccessGitHub());
        vm.Name = "Repo";
        vm.Owner = "octo";
        vm.RepositoryName = "scripts";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);
        vm.RepositoryName = "other-scripts"; // the verdict no longer describes what is being saved

        await vm.SaveCommand.ExecuteAsync(null);

        await _repositories.Received(1).UpsertAsync(
            Arg.Is<GitRepositoryProfile>(p =>
                p.LastValidationStatus == RepositoryValidationStatus.Unvalidated
                && p.LastValidatedAt == null
                && p.LastValidationDetail == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_OfAnUnchangedEditWithoutRevalidating_KeepsTheStoredOutcome()
    {
        var validatedAt = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var vm = NewViewModel();
        vm.LoadForEdit(new GitRepositoryProfile
        {
            Name = "Repo", Owner = "octo", RepositoryName = "scripts", DefaultBranch = "main",
            LastValidationStatus = RepositoryValidationStatus.Attention,
            LastValidatedAt = validatedAt,
            LastValidationDetail = "Read-only access",
        });
        vm.Name = "Renamed"; // a rename does not invalidate the repository's validation

        await vm.SaveCommand.ExecuteAsync(null);

        await _repositories.Received(1).UpsertAsync(
            Arg.Is<GitRepositoryProfile>(p =>
                p.LastValidationStatus == RepositoryValidationStatus.Attention
                && p.LastValidatedAt == validatedAt
                && p.LastValidationDetail == "Read-only access"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_OnAnUnchangedEdit_RecordsTheOutcomeImmediately()
    {
        var profile = new GitRepositoryProfile
        {
            Name = "Repo", Owner = "octo", RepositoryName = "scripts", DefaultBranch = "main",
        };
        var vm = NewViewModel(FullAccessGitHub());
        vm.LoadForEdit(profile);
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        await _repositories.Received(1).UpdateValidationStatusAsync(
            profile.Id, RepositoryValidationStatus.Valid, Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_WithUnsavedCoordinateChanges_DoesNotOverwriteTheStoredRow()
    {
        var profile = new GitRepositoryProfile
        {
            Name = "Repo", Owner = "octo", RepositoryName = "scripts", DefaultBranch = "main",
        };
        var vm = NewViewModel(FullAccessGitHub());
        vm.LoadForEdit(profile);
        vm.RepositoryName = "other-scripts"; // validating coordinates the row does not have yet
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        await _repositories.DidNotReceive().UpdateValidationStatusAsync(
            Arg.Any<Guid>(), Arg.Any<RepositoryValidationStatus>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
