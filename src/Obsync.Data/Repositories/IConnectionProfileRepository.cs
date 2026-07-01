using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for reusable SQL Server connection profiles.</summary>
public interface IConnectionProfileRepository
{
    Task<IReadOnlyList<SqlConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SqlConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertAsync(SqlConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of a connectivity test without touching the rest of the profile.</summary>
    Task UpdateTestStatusAsync(
        Guid id, ConnectionTestStatus status, DateTimeOffset testedAt, string? detail,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
