using Obsync.App.ViewModels;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Models;
using Octokit;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// What the Dashboard says about a repository, and whether it agrees with the Repositories page.
/// </summary>
public sealed class RepositoryAttentionRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static GitRepositoryProfile Repo(RepositoryValidationStatus status, int daysAgo) => new()
    {
        Name = "schema-history",
        Owner = "acme",
        RepositoryName = "schema",
        LastValidationStatus = status,
        LastValidatedAt = Now.AddDays(-daysAgo),
        LastValidationDetail = "Read-only access — Direct Commit and Pull Request jobs will fail to push.",
    };

    private static IReadOnlyList<AttentionItem> Rows(GitRepositoryProfile repository) =>
        AttentionModel.Build([], [], [repository], new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), Now);

    private static IEnumerable<string> Messages(GitRepositoryProfile repository) =>
        Rows(repository).Select(r => r.Text);

    [Fact]
    public void AReadOnlyToken_RaisesARow()
    {
        // Attention is the verdict for a read-only token — the exact failure this checker was built
        // to catch, since every push-based job on it fails. It raised nothing at all: the page showed
        // an amber pill and the Dashboard was silent.
        var rows = Rows(Repo(RepositoryValidationStatus.Attention, daysAgo: 1));

        var row = Assert.Single(rows);
        Assert.Equal(AttentionSeverity.Warning, row.Severity);
        Assert.Contains("needs attention", row.Text);
    }

    [Fact]
    public void AStaleFailure_DoesNotContradictThePage()
    {
        // The page decays a 40-day-old verdict to "Not validated"; the Dashboard read the RAW status
        // and shouted "failed its last check" in red for the same row. One of them had to be wrong.
        var messages = Messages(Repo(RepositoryValidationStatus.Failed, daysAgo: 40)).ToList();

        Assert.DoesNotContain(messages, m => m.Contains("failed its last check"));
        Assert.Contains(messages, m => m.Contains("has not been checked since"));
    }

    [Fact]
    public void AStaleFailure_RaisesExactlyOneRow()
    {
        // It used to satisfy both the failed loop and the staleness loop, so one repository produced
        // two rows for one underlying fact.
        Assert.Single(Rows(Repo(RepositoryValidationStatus.Failed, daysAgo: 40)));
    }

    [Fact]
    public void ARecentFailure_StillRaisesAnError()
    {
        // The decay fix must not silence a genuine, current failure.
        var row = Assert.Single(Rows(Repo(RepositoryValidationStatus.Failed, daysAgo: 1)));

        Assert.Equal(AttentionSeverity.Error, row.Severity);
        Assert.Contains("failed its last check", row.Text);
    }

    [Fact]
    public void AHealthyRecentCheck_RaisesNothing()
    {
        Assert.Empty(Rows(Repo(RepositoryValidationStatus.Valid, daysAgo: 1)));
    }

    [Fact]
    public void TheOctokitHierarchy_StillRequiresRateLimitsToBeCaughtFirst()
    {
        // The whole 403 fix rests on this shape, and it is not obvious from reading the catch block:
        //
        //   ForbiddenException is a SIBLING of AuthorizationException, so the 401 arm never covered
        //   a 403 — which is how an SSO deauthorization went on showing a green "Valid" badge.
        //
        //   All three rate-limit types DERIVE from ForbiddenException, so they must be caught before
        //   it or a rate limit would be recorded as a rejected credential. The C# compiler enforces
        //   that ordering (CS0160), but nothing explains WHY the order is what it is — this does.
        Assert.False(typeof(AuthorizationException).IsAssignableFrom(typeof(ForbiddenException)));

        Assert.True(typeof(ForbiddenException).IsAssignableFrom(typeof(RateLimitExceededException)));
        Assert.True(typeof(ForbiddenException).IsAssignableFrom(typeof(SecondaryRateLimitExceededException)));
        Assert.True(typeof(ForbiddenException).IsAssignableFrom(typeof(AbuseException)));
    }
}
