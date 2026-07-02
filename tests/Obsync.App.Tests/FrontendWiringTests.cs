using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Results;

namespace Obsync.App.Tests;

/// <summary>
/// Regression tests for two frontend wiring defects found in review: editing a job used to wipe
/// fields the wizard doesn't surface, and testing an edited SQL-login connection used to send a
/// blank password instead of the stored secret.
/// </summary>
public sealed class FrontendWiringTests
{
    [Fact]
    public async Task EditingAJob_PreservesFieldsTheWizardDoesNotSurface()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        var connections = Substitute.For<IConnectionProfileRepository>();
        connections.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SqlConnectionProfile>>(
                [new SqlConnectionProfile { Id = connectionId, Name = "Prod", ServerName = "SVR" }]));

        var repositories = Substitute.For<IRepositoryProfileRepository>();
        repositories.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<GitRepositoryProfile>>(
                [new GitRepositoryProfile { Id = repositoryId, Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" }]));

        SyncJob? saved = null;
        var jobs = Substitute.For<IJobRepository>();
        jobs.UpsertAsync(Arg.Do<SyncJob>(j => saved = j), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

        var vm = new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(), Substitute.For<ICredentialStore>(), clock,
            Substitute.For<IAuditWriter>());
        await vm.LoadAsync();

        var existing = new SyncJob
        {
            Name = "Old name",
            ConnectionProfileId = connectionId,
            RepositoryProfileId = repositoryId,
            Databases = ["db1"],
            Branch = "main",
            Description = "keep me",
            Enabled = false,
            Advanced = new JobAdvancedOptions { SqlRetryCount = 7, SqlCommandTimeoutSeconds = 999 },
            RunSummary = new JobRunSummary { LastStatus = RunStatus.Succeeded, LastChangeCount = 42 },
            Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.Recommended, IncludePermissions = false },
            Schedule = new ScheduleProfile { Kind = ScheduleKind.Manual },
        };

        vm.InitializeForEdit(existing);
        vm.Name = "New name"; // change a wizard-surfaced field

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal("New name", saved!.Name);           // wizard field applied
        Assert.Equal("keep me", saved.Description);       // preserved
        Assert.False(saved.Enabled);                      // preserved (not re-enabled)
        Assert.Equal(7, saved.Advanced.SqlRetryCount);    // preserved
        Assert.Equal(999, saved.Advanced.SqlCommandTimeoutSeconds);
        Assert.Equal(RunStatus.Succeeded, saved.RunSummary.LastStatus); // run summary not wiped
        Assert.Equal(42, saved.RunSummary.LastChangeCount);
        Assert.False(saved.Selection.IncludePermissions); // non-preset selection setting preserved
    }

    [Fact]
    public async Task TestingAnEditedSqlLogin_UsesTheStoredPassword()
    {
        var profile = new SqlConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Prod",
            ServerName = "SVR",
            AuthenticationMode = SqlAuthenticationMode.SqlLogin,
            Username = "sa",
        };

        var repository = Substitute.For<IConnectionProfileRepository>();

        var credentials = Substitute.For<ICredentialStore>();
        credentials.Retrieve(CredentialKeys.SqlPassword(profile.Id)).Returns("stored-secret");

        string? passwordSentToProbe = null;
        var probe = Substitute.For<ISqlServerProbe>();
        probe.TestConnectionAsync(Arg.Any<SqlConnectionProfile>(), Arg.Do<string?>(p => passwordSentToProbe = p), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SqlServerInfo>("ignored — we only assert the password"));

        var vm = new ServerDialogViewModel(repository, probe, credentials, Substitute.For<IClock>(), Substitute.For<IAuditWriter>());
        vm.LoadForEdit(profile);   // blanks the password (keep-existing-secret semantics), enters edit mode
        await vm.TestCommand.ExecuteAsync(null);

        Assert.Equal("stored-secret", passwordSentToProbe);
    }
}
