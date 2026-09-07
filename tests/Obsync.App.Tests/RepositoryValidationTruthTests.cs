using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The Repositories page must never assert a credential is good when it has evidence otherwise.
/// </summary>
/// <remarks>
/// The badge is a last-successful-validation claim that decays after 30 days, and a check that
/// could not run deliberately leaves it alone — a network blink says nothing about a token. The
/// defect was in where that line was drawn: two conditions that ARE evidence were being treated as
/// "could not run", so a repository kept showing green "Valid" for up to a month while every push
/// failed.
/// </remarks>
public sealed class RepositoryValidationTruthTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();
    private readonly IRepositoryProfileRepository _repositories = Substitute.For<IRepositoryProfileRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly Obsync.App.Services.IWorkspaceReclaimer _workspaces =
        Substitute.For<Obsync.App.Services.IWorkspaceReclaimer>();

    private readonly GitRepositoryProfile _profile = new()
    {
        Name = "AZUVNSQLDTDB009 Repo",
        Owner = "acme",
        RepositoryName = "schema",
        DefaultBranch = "main",
        LastValidationStatus = RepositoryValidationStatus.Valid,
        LastValidatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        LastValidationDetail = "Read and write access verified — authenticated as alice.",
    };

    private RepositoriesViewModel Build()
    {
        _repositories.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitRepositoryProfile>>([_profile]));
        return new RepositoriesViewModel(
            _repositories, _gitHub, _credentials, new SystemClock(), _audit, _workspaces);
    }

    [Fact]
    public async Task NoTokenSaved_RecordsFailed_RatherThanLeavingTheBadgeGreen()
    {
        // The most certain evidence the product ever has — more certain than anything GitHub could
        // say. It used to return without touching the stored verdict, so a repository whose token
        // had been removed from Credential Manager went on displaying "Valid".
        _credentials.Retrieve(CredentialKeys.GitHubToken(_profile.Id)).Returns((string?)null);

        await Build().CheckTokenCommand.ExecuteAsync(_profile);

        await _repositories.Received(1).UpdateValidationStatusAsync(
            _profile.Id, RepositoryValidationStatus.Failed, Arg.Any<DateTimeOffset>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ACheckThatCouldNotRun_SaysTheStatusIsUnchanged()
    {
        // The stored verdict is correctly left alone — but the page then shows the previous verdict
        // beside a failure message, and nothing reconciled the two. The message has to.
        _credentials.Retrieve(CredentialKeys.GitHubToken(_profile.Id)).Returns("tok");
        _gitHub.CheckRepositoryAccessAsync("tok", "acme", "schema", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TokenPermissionReport>("Could not reach GitHub: connection reset."));

        var vm = Build();
        await vm.CheckTokenCommand.ExecuteAsync(_profile);

        await _repositories.DidNotReceive().UpdateValidationStatusAsync(
            Arg.Any<Guid>(), Arg.Any<RepositoryValidationStatus>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>());
        Assert.Contains("has not been changed", vm.StatusMessage);
    }

    [Fact]
    public async Task ARefusedToken_RecordsFailed()
    {
        // A 403 report reaches the view model as a successful CHECK carrying a negative VERDICT.
        _credentials.Retrieve(CredentialKeys.GitHubToken(_profile.Id)).Returns("tok");
        _gitHub.CheckRepositoryAccessAsync("tok", "acme", "schema", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: null, RepositoryFound: false, CanRead: false, CanWrite: false,
                Detail: "GitHub refused this token for acme/schema (HTTP 403).")));

        await Build().CheckTokenCommand.ExecuteAsync(_profile);

        await _repositories.Received(1).UpdateValidationStatusAsync(
            _profile.Id, RepositoryValidationStatus.Failed, Arg.Any<DateTimeOffset>(), Arg.Any<string?>());
    }
}
