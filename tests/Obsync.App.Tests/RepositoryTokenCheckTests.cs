using System.Linq;
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
/// Covers the token permission checker's UI behavior — most importantly that a token which can read
/// but NOT write is clearly flagged. A read-only token used to validate as "OK" and then silently
/// fail every push; this is the regression guard for that. Also covers the default-branch existence
/// check that rides the same validation, and the persisted outcome.
/// </summary>
public sealed class RepositoryTokenCheckTests
{
    private static RepositoryDialogViewModel CreateVm(IGitHubService gitHub) => new(
        Substitute.For<IRepositoryProfileRepository>(), gitHub,
        Substitute.For<ICredentialStore>(), Substitute.For<IClock>(), Substitute.For<IAuditWriter>());

    private static IGitHubService GitHub(
        bool canWrite = true, IReadOnlyList<string>? branches = null, string? branchError = null)
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: canWrite, Detail: null)));
        gitHub.GetBranchesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(branchError is null
                ? Result.Success(branches ?? ["main"])
                : Result.Failure<IReadOnlyList<string>>(branchError));
        return gitHub;
    }

    [Fact]
    public async Task Validate_FlagsAReadOnlyTokenAsModeDependent()
    {
        var vm = CreateVm(GitHub(canWrite: false));
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        var write = vm.PermissionChecks.Single(c => c.Label.StartsWith("Write"));
        Assert.False(write.Ok);
        Assert.Contains("Read-only access", vm.ValidationResult);
        Assert.Contains("Direct Commit and Pull Request jobs will fail to push", vm.ValidationResult);
        Assert.Contains("Local Commit Only and Export Only are unaffected", vm.ValidationResult);
    }

    [Fact]
    public async Task Validate_AllGreen_ReportsVerifiedAccessAndTheBranch()
    {
        var vm = CreateVm(GitHub());
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.All(vm.PermissionChecks, c => Assert.True(c.Ok));
        var branch = vm.PermissionChecks.Single(c => c.Label.StartsWith("Branch"));
        Assert.Equal("Branch 'main' exists", branch.Label);
        Assert.Contains("Read and write access verified", vm.ValidationResult);
        Assert.Contains("alice", vm.ValidationResult);
    }

    [Fact]
    public async Task Validate_MissingDefaultBranch_FailsWithTheBranchNamed()
    {
        var vm = CreateVm(GitHub(branches: ["main", "release"]));
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.DefaultBranch = "develop";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        var branch = vm.PermissionChecks.Single(c => c.Label.StartsWith("Branch"));
        Assert.False(branch.Ok);
        Assert.Contains("Branch 'develop' not found in company/schema", vm.ValidationResult);
    }

    [Fact]
    public async Task Validate_BranchMatchIsCaseSensitive()
    {
        // Git branch names are case-sensitive; "Main" must not satisfy a job targeting "main".
        var vm = CreateVm(GitHub(branches: ["Main"]));
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Contains("Branch 'main' not found in company/schema", vm.ValidationResult);
    }

    [Fact]
    public async Task Validate_BranchListingFailure_IsAWarningNotAFailure()
    {
        var vm = CreateVm(GitHub(branchError: "GitHub error: boom"));
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        // The verdict stands; the branch line is absent (unknown) and the warning is surfaced.
        Assert.Contains("Read and write access verified", vm.ValidationResult);
        Assert.Contains("Could not verify that branch 'main' exists", vm.ValidationResult);
        Assert.DoesNotContain(vm.PermissionChecks, c => c.Label.StartsWith("Branch"));
    }

    [Fact]
    public async Task Validate_WithoutOwnerOrRepo_AsksForThemAndDoesNotCallGitHub()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var vm = CreateVm(gitHub);
        vm.Token = "tok"; // owner/repo left blank

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Empty(vm.PermissionChecks);
        await gitHub.DidNotReceive().CheckRepositoryAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true, RepositoryValidationStatus.Failed)]   // invalid token
    [InlineData(true, false, RepositoryValidationStatus.Attention)] // read-only token
    [InlineData(true, true, RepositoryValidationStatus.Valid)]
    public async Task CheckToken_PersistsTheValidationOutcome(
        bool tokenValid, bool canWrite, RepositoryValidationStatus expected)
    {
        var profile = new GitRepositoryProfile { Name = "R", Owner = "company", RepositoryName = "schema" };

        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync("tok", "company", "schema", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                tokenValid, Login: tokenValid ? "alice" : null, RepositoryFound: tokenValid,
                CanRead: tokenValid, CanWrite: tokenValid && canWrite, Detail: null)));
        gitHub.GetBranchesAsync("tok", "company", "schema", Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>(["main"]));

        var repositories = Substitute.For<IRepositoryProfileRepository>();
        var credentials = Substitute.For<ICredentialStore>();
        credentials.Retrieve(Arg.Any<string>()).Returns("tok");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

        var vm = new RepositoriesViewModel(repositories, gitHub, credentials, clock, Substitute.For<IAuditWriter>());
        await vm.CheckTokenCommand.ExecuteAsync(profile);

        await repositories.Received(1).UpdateValidationStatusAsync(
            profile.Id, expected, DateTimeOffset.UnixEpoch, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckToken_MissingDefaultBranch_PersistsFailed()
    {
        var profile = new GitRepositoryProfile
        {
            Name = "R", Owner = "company", RepositoryName = "schema", DefaultBranch = "develop",
        };

        var repositories = Substitute.For<IRepositoryProfileRepository>();
        var vm = new RepositoriesViewModel(
            repositories, GitHub(branches: ["main"]), CredentialsWithToken(),
            Substitute.For<IClock>(), Substitute.For<IAuditWriter>());

        await vm.CheckTokenCommand.ExecuteAsync(profile);

        await repositories.Received(1).UpdateValidationStatusAsync(
            profile.Id, RepositoryValidationStatus.Failed, Arg.Any<DateTimeOffset>(),
            "Branch 'develop' not found in company/schema.", Arg.Any<CancellationToken>());
        Assert.Contains("Branch 'develop' not found", vm.StatusMessage);
    }

    [Fact]
    public async Task CheckToken_WhenTheCheckCannotRun_LeavesTheStoredOutcomeUntouched()
    {
        var profile = new GitRepositoryProfile { Name = "R", Owner = "company", RepositoryName = "schema" };

        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TokenPermissionReport>("Could not reach GitHub: offline"));

        var repositories = Substitute.For<IRepositoryProfileRepository>();
        var vm = new RepositoriesViewModel(
            repositories, gitHub, CredentialsWithToken(), Substitute.For<IClock>(), Substitute.For<IAuditWriter>());

        await vm.CheckTokenCommand.ExecuteAsync(profile);

        await repositories.DidNotReceive().UpdateValidationStatusAsync(
            Arg.Any<Guid>(), Arg.Any<RepositoryValidationStatus>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Contains("Could not reach GitHub", vm.StatusMessage);
    }

    private static ICredentialStore CredentialsWithToken()
    {
        var credentials = Substitute.For<ICredentialStore>();
        credentials.Retrieve(Arg.Any<string>()).Returns("tok");
        return credentials;
    }
}
