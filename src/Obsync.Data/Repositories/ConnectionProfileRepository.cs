using Dapper;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <inheritdoc cref="IConnectionProfileRepository" />
public sealed class ConnectionProfileRepository : IConnectionProfileRepository
{
    private const string SelectColumns = """
        SELECT id AS Id, name AS Name, server_name AS ServerName, auth_mode AS AuthenticationMode,
               username AS Username, encrypt AS Encrypt, trust_server_certificate AS TrustServerCertificate,
               connect_timeout_seconds AS ConnectTimeoutSeconds, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM connection_profiles
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ConnectionProfileRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<SqlConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SqlConnectionProfile>(
            new CommandDefinition($"{SelectColumns} ORDER BY name;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
    }

    public async Task<SqlConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<SqlConnectionProfile>(
            new CommandDefinition($"{SelectColumns} WHERE id = $id;", new { id = id.ToString() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(SqlConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO connection_profiles
                (id, name, server_name, auth_mode, username, encrypt, trust_server_certificate,
                 connect_timeout_seconds, created_at, updated_at)
            VALUES
                ($id, $name, $server, $auth, $user, $encrypt, $trust, $timeout, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, server_name = excluded.server_name, auth_mode = excluded.auth_mode,
                username = excluded.username, encrypt = excluded.encrypt,
                trust_server_certificate = excluded.trust_server_certificate,
                connect_timeout_seconds = excluded.connect_timeout_seconds, updated_at = excluded.updated_at;
            """,
            new
            {
                id = profile.Id.ToString(),
                name = profile.Name,
                server = profile.ServerName,
                auth = (int)profile.AuthenticationMode,
                user = profile.Username,
                encrypt = profile.Encrypt ? 1 : 0,
                trust = profile.TrustServerCertificate ? 1 : 0,
                timeout = profile.ConnectTimeoutSeconds,
                created = profile.CreatedAt,
                updated = profile.UpdatedAt,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM connection_profiles WHERE id = $id;",
            new { id = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
