using System.Diagnostics.CodeAnalysis;

namespace Obsync.Shared.Results;

/// <summary>
/// A lightweight outcome type used across the engine so that expected failures
/// (a failed connection test, an invalid token) are modelled as data rather than
/// exceptions. Use <see cref="Result{T}"/> when a value is produced on success.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, string? error)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed result must carry an error message.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The human-readable error message when <see cref="IsFailure"/>; otherwise null.</summary>
    public string? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(string error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Failure<T>(string error) => Result<T>.Fail(error);
}

/// <summary>A <see cref="Result"/> that carries a value when successful.</summary>
public sealed class Result<T> : Result
{
    private readonly T _value;

    private Result(bool isSuccess, T value, string? error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>The produced value. Throws if accessed on a failed result.</summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("Cannot access the value of a failed result.");

    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(string error) => new(false, default!, error);

    /// <summary>Returns the value on success, or <paramref name="fallback"/> on failure.</summary>
    public T ValueOr(T fallback) => IsSuccess ? _value : fallback;

    /// <summary>Exposes the value without throwing, for pattern-style consumption.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = IsSuccess ? _value : default;
        return IsSuccess;
    }
}
