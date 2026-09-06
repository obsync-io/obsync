using Obsync.Shared.Models;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// A green "Valid" pill used to be permanent: nothing re-evaluated it, and the age of the check
/// lived only in a hover tooltip. A token validated in January and expired in March still read as
/// Valid in September. The badge is the surface people actually read, so the badge has to decay.
/// </summary>
public sealed class RepositoryValidationDecayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static GitRepositoryProfile Validated(RepositoryValidationStatus status, DateTimeOffset? at) =>
        new() { Name = "R", Owner = "o", RepositoryName = "r", LastValidationStatus = status, LastValidatedAt = at };

    [Fact]
    public void ARecentCheck_StillCounts()
    {
        var repository = Validated(RepositoryValidationStatus.Valid, Now.AddDays(-3));

        Assert.False(repository.IsValidationStale(Now));
        Assert.Equal(RepositoryValidationStatus.Valid, repository.EffectiveValidationStatus(Now));
    }

    [Fact]
    public void AnOldCheck_StopsAssertingHealth()
    {
        var repository = Validated(RepositoryValidationStatus.Valid, Now.AddDays(-60));

        Assert.True(repository.IsValidationStale(Now));
        Assert.Equal(RepositoryValidationStatus.Unvalidated, repository.EffectiveValidationStatus(Now));
    }

    [Fact]
    public void AFailedCheck_AlsoDecays_RatherThanAssertingAStaleFailure()
    {
        // Symmetry matters: a two-month-old failure is no more current than a two-month-old success,
        // and leaving it red forever would train people to ignore the colour.
        var repository = Validated(RepositoryValidationStatus.Failed, Now.AddDays(-90));

        Assert.Equal(RepositoryValidationStatus.Unvalidated, repository.EffectiveValidationStatus(Now));
    }

    [Fact]
    public void AStatusWithNoTimestamp_IsTreatedAsStale()
    {
        // Rows written before the timestamp existed claim a verdict they cannot date.
        var repository = Validated(RepositoryValidationStatus.Valid, null);

        Assert.True(repository.IsValidationStale(Now));
    }

    [Fact]
    public void NeverValidated_IsNotStale_ItIsSimplyUnvalidated()
    {
        // Otherwise every freshly added repository would raise an attention row saying its check is
        // old, which is not what "never checked" means.
        var repository = Validated(RepositoryValidationStatus.Unvalidated, null);

        Assert.False(repository.IsValidationStale(Now));
    }

    [Fact]
    public void TheBoundaryIsThirtyDays()
    {
        Assert.False(Validated(RepositoryValidationStatus.Valid, Now.AddDays(-30).AddMinutes(1)).IsValidationStale(Now));
        Assert.True(Validated(RepositoryValidationStatus.Valid, Now.AddDays(-30).AddMinutes(-1)).IsValidationStale(Now));
    }
}
