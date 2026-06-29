using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Obsync.Shared.Models;
using Obsync.Shared.Results;

namespace Obsync.Metadata;

/// <summary>Tests connectivity and enumerates databases on a SQL Server instance.</summary>
public interface ISqlServerProbe
{
    Task<Result<SqlServerInfo>> TestConnectionAsync(
        SqlConnectionProfile profile, string? password, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SqlDatabaseInfo>>> GetDatabasesAsync(
        SqlConnectionProfile profile, string? password, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISqlServerProbe" />
public sealed class SqlServerProbe : ISqlServerProbe
{
    private readonly ISqlConnectionStringFactory _connectionStrings;
    private readonly ILogger<SqlServerProbe> _logger;

    public SqlServerProbe(ISqlConnectionStringFactory connectionStrings, ILogger<SqlServerProbe> logger)
    {
        _connectionStrings = connectionStrings;
        _logger = logger;
    }

    public async Task<Result<SqlServerInfo>> TestConnectionAsync(
        SqlConnectionProfile profile, string? password, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionStrings.Create(profile, password));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))," +
                " CAST(SERVERPROPERTY('Edition') AS nvarchar(256))," +
                " CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128));";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<SqlServerInfo>("The server did not return version information.");
            }

            var version = reader.GetString(0);
            var major = int.TryParse(version.Split('.')[0], out var m) ? m : 0;

            return Result.Success(new SqlServerInfo
            {
                ProductVersion = version,
                Edition = reader.GetString(1),
                ProductLevel = reader.GetString(2),
                MajorVersion = major,
            });
        }
        catch (SqlException ex)
        {
            _logger.LogWarning("Connection test to {Server} failed: {Message}", profile.ServerName, ex.Message);
            return Result.Failure<SqlServerInfo>(FriendlyMessage(ex));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Result.Failure<SqlServerInfo>(ex.Message);
        }
    }

    public async Task<Result<IReadOnlyList<SqlDatabaseInfo>>> GetDatabasesAsync(
        SqlConnectionProfile profile, string? password, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionStrings.Create(profile, password));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT d.name AS Name,
                       d.state AS State,
                       CAST(ISNULL(SUM(CAST(mf.size AS bigint)) * 8 / 1024, 0) AS bigint) AS SizeMb
                FROM sys.databases d
                LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
                WHERE d.database_id > 4 AND d.name <> 'distribution'
                GROUP BY d.name, d.state
                ORDER BY d.name;
                """;

            var databases = new List<SqlDatabaseInfo>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                databases.Add(new SqlDatabaseInfo
                {
                    Name = reader.GetString(0),
                    IsOnline = reader.GetByte(1) == 0,
                    SizeMb = reader.GetInt64(2),
                });
            }

            return Result.Success<IReadOnlyList<SqlDatabaseInfo>>(databases);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning("Enumerating databases on {Server} failed: {Message}", profile.ServerName, ex.Message);
            return Result.Failure<IReadOnlyList<SqlDatabaseInfo>>(FriendlyMessage(ex));
        }
    }

    private static string FriendlyMessage(SqlException ex) => ex.Number switch
    {
        18456 => "Login failed. Check the username and password.",
        53 or -1 => "Could not reach the server. Check the server name and that it is accessible.",
        -2 => "The connection timed out. The server may be busy or unreachable.",
        4060 => "Could not open the database with the supplied credentials.",
        _ => $"SQL error {ex.Number}: {ex.Message}",
    };
}
