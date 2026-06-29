using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for reusable SQL Server connection profiles.</summary>
public interface IConnectionProfileRepository
{
    Task<IReadOnlyList<SqlConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SqlConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertAsync(SqlConnectionProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
