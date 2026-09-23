using Obsync.Shared;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// The seconds-to-milliseconds conversion behind <c>SET LOCK_TIMEOUT</c>.
/// </summary>
/// <remarks>
/// Three call sites computed this as <c>seconds * 1000</c> in unchecked int arithmetic, so a large
/// configured or imported value wrapped NEGATIVE — and SQL Server rejects every negative value
/// except -1, so the session setup threw on each metadata connection and every database in the job
/// failed. The intent of the setting is to fail FAST on a blocked read; the overflow inverted it.
/// </remarks>
public sealed class SqlLockTimeoutTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1_000)]
    [InlineData(30, 30_000)]
    [InlineData(86_400, 86_400_000)]
    public void OrdinaryValues_ConvertExactly(int seconds, int expected) =>
        Assert.Equal(expected, SqlLockTimeout.ToMilliseconds(seconds));

    [Theory]
    [InlineData(2_147_484)]   // the first value that overflowed
    [InlineData(3_000_000)]   // produced SET LOCK_TIMEOUT -1294967296
    [InlineData(int.MaxValue)]
    public void ValuesThatWouldOverflow_SaturateInsteadOfGoingNegative(int seconds)
    {
        var milliseconds = SqlLockTimeout.ToMilliseconds(seconds);

        Assert.Equal(int.MaxValue, milliseconds);

        // The property that actually matters: SQL Server treats -1 as "wait forever" and rejects
        // every other negative, so a negative here is either the exact opposite of what was asked
        // for or a hard failure of every database in the job.
        Assert.True(milliseconds > 0, "a lock timeout must never be emitted as a negative value");
    }

    [Fact]
    public void TheBoundaryIsNotOffByOne()
    {
        Assert.Equal(2_147_483_000, SqlLockTimeout.ToMilliseconds(2_147_483));
        Assert.Equal(int.MaxValue, SqlLockTimeout.ToMilliseconds(2_147_484));
    }
}
