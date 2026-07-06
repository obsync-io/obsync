using Obsync.Shared.Models;

namespace Obsync.Shared.Scripting;

/// <summary>
/// The outcome of scripting one reference table's data: a deterministic INSERT script, or a
/// human-readable reason it was skipped (missing table, over the row cap, no deterministic order).
/// </summary>
public sealed record ReferenceDataResult
{
    public string? Script { get; private init; }
    public string? SkipReason { get; private init; }
    public long RowCount { get; private init; }

    public static ReferenceDataResult Scripted(string script, long rowCount) =>
        new() { Script = script, RowCount = rowCount };

    public static ReferenceDataResult Skipped(string reason) => new() { SkipReason = reason };
}

/// <summary>
/// Scripts a single table's rows as deterministic, re-runnable INSERT statements for versioning
/// reference/static data alongside the schema scripts.
/// </summary>
public interface IReferenceDataReader
{
    Task<ReferenceDataResult> ReadTableDataAsync(
        SqlConnectionProfile profile, string? password, string database, string schema, string table,
        int maxRows, int commandTimeoutSeconds, int lockTimeoutSeconds = 0,
        CancellationToken cancellationToken = default);
}
