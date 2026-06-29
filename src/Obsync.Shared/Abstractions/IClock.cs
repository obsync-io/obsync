namespace Obsync.Shared.Abstractions;

/// <summary>Abstracts the system clock so time-dependent logic stays testable.</summary>
public interface IClock
{
    /// <summary>The current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The default <see cref="IClock"/> backed by the system clock.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
