using System.Linq;
using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// Covers the token permission checker's UI behavior — most importantly that a token which can read
/// but NOT write is clearly flagged. A read-only token used to validate as "OK" and then silently
/// fail every push; this is the regression guard for that.
/// </summary>
public sealed class RepositoryTokenCheckTests
{
    private static RepositoriesViewModel CreateVm(IGitHubService gitHub) => new(
        Substitute.For<IRepositoryProfileRepository>(), gitHub,
        Substitute.For<ICredentialStore>(), Substitute.For<IClock>(), Substitute.For<IAuditWriter>());

    [Fact]
    public async Task Validate_FlagsAReadOnlyTokenAsUnableToWrite()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync("tok", "company", "schema", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: false, Detail: null)));

        var vm = CreateVm(gitHub);
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        var write = vm.PermissionChecks.Single(c => c.Label.StartsWith("Write"));
        Assert.False(write.Ok);
        Assert.Contains("Pushes will fail", vm.ValidationResult);
    }

    [Fact]
    public async Task Validate_AllGreen_ReportsAuthenticatedUser()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.CheckRepositoryAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: "alice", RepositoryFound: true, CanRead: true, CanWrite: true, Detail: null)));

        var vm = CreateVm(gitHub);
        vm.Owner = "company";
        vm.RepositoryName = "schema";
        vm.Token = "tok";

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.All(vm.PermissionChecks, c => Assert.True(c.Ok));
        Assert.Contains("alice", vm.ValidationResult);
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
}
