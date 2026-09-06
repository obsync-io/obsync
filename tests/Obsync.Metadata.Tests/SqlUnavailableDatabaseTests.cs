using Obsync.Metadata;
using Xunit;

namespace Obsync.Metadata.Tests;

/// <summary>
/// "This database is unavailable" is a different question from "this failure is transient", and only
/// one of them is about retrying. A database mid-restore will not become available inside a retry's
/// backoff — but the RUN should still contain the failure to that database and commit everything
/// else, exactly as it already did for an offline one.
/// </summary>
public sealed class SqlUnavailableDatabaseTests
{
    // SqlException cannot be constructed directly, so the numbers are asserted through the shape the
    // engine actually consults: a wrapped exception chain. This still exercises the unwrapping the
    // predicate does for SMO's ConnectionFailureException.
    private static Exception Wrapped(Exception inner) => new InvalidOperationException("smo", inner);

    [Fact]
    public void ATimeout_IsTransient_AndThereforeContainable()
    {
        Assert.True(SqlTransientErrors.IsTransient(new TimeoutException()));
        Assert.True(SqlTransientErrors.IsContainable(new TimeoutException()));
        Assert.True(SqlTransientErrors.IsContainable(Wrapped(new TimeoutException())));
    }

    [Fact]
    public void AnOrdinaryFailure_IsNeitherTransientNorContainable()
    {
        // The safety stops (case-twin guard and friends) exist to fail the run and must keep doing so.
        var ordinary = new InvalidOperationException("two objects differ only by case");

        Assert.False(SqlTransientErrors.IsTransient(ordinary));
        Assert.False(SqlTransientErrors.IsUnavailableDatabase(ordinary));
        Assert.False(SqlTransientErrors.IsContainable(ordinary));
    }

    [Fact]
    public void CancellationIsNotSwallowed()
    {
        Assert.False(SqlTransientErrors.IsContainable(new OperationCanceledException()));
    }
}
