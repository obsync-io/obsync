using Dapper;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for reusable GitHub repository profiles.</summary>
public interface IRepositoryProfileRepository
{
    Task<IReadOnlyList<GitRepositoryProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<GitRepositoryProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertAsync(GitRepositoryProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of a validation without touching the rest of the profile
    /// (mirror of <see cref="IConnectionProfileRepository.UpdateTestStatusAsync"/>).</summary>
    Task UpdateValidationStatusAsync(
        Guid id, RepositoryValidationStatus status, DateTimeOffset validatedAt, string? detail,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRepositoryProfileRepository" />
public sealed class RepositoryProfileRepository : IRepositoryProfileRepository
{
    private const string SelectColumns = """
        SELECT id AS Id, name AS Name, owner AS Owner, repository_name AS RepositoryName,
               remote_url AS RemoteUrl, default_branch AS DefaultBranch, auth_mode AS AuthMode,
               last_validation_status AS LastValidationStatus, last_validated_at AS LastValidatedAt,
               last_validation_detail AS LastValidationDetail,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM repository_profiles
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public RepositoryProfileRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<GitRepositoryProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GitRepositoryProfile>(
            new CommandDefinition($"{SelectColumns} ORDER BY name;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
    }

    public async Task<GitRepositoryProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<GitRepositoryProfile>(
            new CommandDefinition($"{SelectColumns} WHERE id = $id;", new { id = id.ToString() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(GitRepositoryProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO repository_profiles
                (id, name, owner, repository_name, remote_url, default_branch, auth_mode,
                 last_validation_status, last_validated_at, last_validation_detail, created_at, updated_at)
            VALUES
                ($id, $name, $owner, $repo, $url, $branch, $auth, $status, $validatedAt, $detail, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, owner = excluded.owner, repository_name = excluded.repository_name,
                remote_url = excluded.remote_url, default_branch = excluded.default_branch,
                auth_mode = excluded.auth_mode, last_validation_status = excluded.last_validation_status,
                last_validated_at = excluded.last_validated_at,
                last_validation_detail = excluded.last_validation_detail, updated_at = excluded.updated_at;
            """,
            new
            {
                id = profile.Id.ToString(),
                name = profile.Name,
                owner = profile.Owner,
                repo = profile.RepositoryName,
                url = profile.RemoteUrl,
                branch = profile.DefaultBranch,
                auth = (int)profile.AuthMode,
                status = (int)profile.LastValidationStatus,
                validatedAt = profile.LastValidatedAt,
                detail = profile.LastValidationDetail,
                created = profile.CreatedAt,
                updated = profile.UpdatedAt,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateValidationStatusAsync(
        Guid id, RepositoryValidationStatus status, DateTimeOffset validatedAt, string? detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE repository_profiles
            SET last_validation_status = $status, last_validated_at = $validatedAt, last_validation_detail = $detail
            WHERE id = $id;
            """,
            new { id = id.ToString(), status = (int)status, validatedAt, detail },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM repository_profiles WHERE id = $id;",
            new { id = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
