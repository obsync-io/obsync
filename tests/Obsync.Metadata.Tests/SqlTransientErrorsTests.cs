namespace Obsync.Metadata.Tests;

public sealed class SqlTransientErrorsTests
{
    [Fact]
    public void IsTransient_TimeoutException_IsTrue() =>
        Assert.True(SqlTransientErrors.IsTransient(new TimeoutException()));

    [Fact]
    public void IsTransient_NonSqlException_IsFalse() =>
        Assert.False(SqlTransientErrors.IsTransient(new InvalidOperationException("permission denied")));

    [Fact]
    public async Task RetryAsync_RetriesTransientFailureThenSucceeds()
    {
        var attempts = 0;

        var result = await SqlTransientErrors.RetryAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                {
                    throw new TimeoutException();
                }

                return Task.FromResult(attempts);
            },
            maxAttempts: 3,
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task RetryAsync_NonTransientFailure_DoesNotRetry()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlTransientErrors.RetryAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException();
                },
                maxAttempts: 3,
                CancellationToken.None));

        Assert.Equal(1, attempts);
    }
}
