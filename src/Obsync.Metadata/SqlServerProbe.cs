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

    /// <summary>Lists a database's user tables with approximate row counts (for the reference-data picker).</summary>
    Task<Result<IReadOnlyList<SqlTableInfo>>> GetTablesAsync(
        SqlConnectionProfile profile, string? password, string database, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one object's dependency graph (one level, both directions) from the live catalog:
    /// referencing modules, foreign-key tables, and triggers on one side; referenced entities on the
    /// other. Read-only, like every Obsync query.
    /// </summary>
    Task<Result<SqlObjectDependencies>> GetDependenciesAsync(
        SqlConnectionProfile profile, string? password, string database, string schema, string name,
        CancellationToken cancellationToken = default);
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

    public async Task<Result<IReadOnlyList<SqlTableInfo>>> GetTablesAsync(
        SqlConnectionProfile profile, string? password, string database, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // Row counts come from partition metadata (approximate but instant — no table scans).
            command.CommandText =
                """
                SELECT s.name AS SchemaName, t.name AS TableName,
                       CAST(ISNULL(SUM(p.rows), 0) AS bigint) AS RowCnt
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                WHERE t.is_ms_shipped = 0
                GROUP BY s.name, t.name
                ORDER BY s.name, t.name;
                """;

            var tables = new List<SqlTableInfo>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new SqlTableInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    RowCount = reader.GetInt64(2),
                });
            }

            return Result.Success<IReadOnlyList<SqlTableInfo>>(tables);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning("Enumerating tables in {Database} on {Server} failed: {Message}", database, profile.ServerName, ex.Message);
            return Result.Failure<IReadOnlyList<SqlTableInfo>>(FriendlyMessage(ex));
        }
    }

    public async Task<Result<SqlObjectDependencies>> GetDependenciesAsync(
        SqlConnectionProfile profile, string? password, string database, string schema, string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionStrings.Create(profile, password, database));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@name", name);
            // One round trip: dependents (referencing modules + FK tables + triggers) and referenced
            // entities, each capped so a hub object on a VLDB cannot flood the UI. Cross-database and
            // unresolved references survive via the sql_expression_dependencies name columns.
            command.CommandText =
                """
                DECLARE @id int = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@name));
                IF @id IS NULL
                BEGIN
                    SELECT CAST(0 AS bit) AS ObjectFound;
                    RETURN;
                END;
                SELECT CAST(1 AS bit) AS ObjectFound;

                SELECT DISTINCT TOP (500) o.type_desc AS TypeDesc, s.name AS SchemaName, o.name AS ObjectName
                FROM sys.sql_expression_dependencies d
                JOIN sys.objects o ON o.object_id = d.referencing_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE d.referenced_id = @id AND d.referencing_id <> @id
                UNION
                SELECT DISTINCT 'OBSYNC_FK_TABLE', s.name, t.name
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON t.object_id = fk.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE fk.referenced_object_id = @id
                UNION
                SELECT DISTINCT 'SQL_TRIGGER', s.name, o.name
                FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.parent_object_id = @id AND o.type = 'TR'
                ORDER BY 2, 3;

                SELECT DISTINCT TOP (500)
                       CASE
                           WHEN d.referenced_database_name IS NOT NULL THEN 'OBSYNC_CROSS_DB'
                           WHEN o.object_id IS NULL THEN 'OBSYNC_UNRESOLVED'
                           ELSE o.type_desc
                       END AS TypeDesc,
                       ISNULL(s.name, ISNULL(d.referenced_schema_name, '')) AS SchemaName,
                       CASE
                           WHEN d.referenced_database_name IS NOT NULL
                               THEN d.referenced_database_name + N'.' + ISNULL(d.referenced_schema_name + N'.', N'') + d.referenced_entity_name
                           ELSE COALESCE(o.name, d.referenced_entity_name)
                       END AS ObjectName
                FROM sys.sql_expression_dependencies d
                LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
                LEFT JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE d.referencing_id = @id AND (d.referenced_id IS NULL OR d.referenced_id <> @id)
                UNION
                SELECT DISTINCT 'OBSYNC_FK_TABLE', s.name, t.name
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON t.object_id = fk.referenced_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE fk.parent_object_id = @id AND fk.referenced_object_id <> @id
                ORDER BY 2, 3;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0))
            {
                return Result.Failure<SqlObjectDependencies>(
                    $"{schema}.{name} was not found in {database} — it may have been dropped or renamed since the last sync.");
            }

            var usedBy = new List<SqlDependencyItem>();
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                usedBy.Add(MapDependency(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            var uses = new List<SqlDependencyItem>();
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                uses.Add(MapDependency(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            return Result.Success(new SqlObjectDependencies { UsedBy = usedBy, Uses = uses });
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(
                "Dependency lookup for {Schema}.{Name} in {Database} failed: {Message}", schema, name, database, ex.Message);
            return Result.Failure<SqlObjectDependencies>(FriendlyMessage(ex));
        }
    }

    private static SqlDependencyItem MapDependency(string typeDesc, string schema, string name) => new()
    {
        Schema = schema,
        Name = name,
        TypeLabel = DependencyTypeLabel(typeDesc),
        IsDrillable = typeDesc is not ("OBSYNC_CROSS_DB" or "OBSYNC_UNRESOLVED"),
    };

    /// <summary>Friendly label for a catalog type_desc (plus Obsync's own relationship markers).</summary>
    internal static string DependencyTypeLabel(string typeDesc) => typeDesc switch
    {
        "USER_TABLE" => "Table",
        "VIEW" => "View",
        "SQL_STORED_PROCEDURE" or "CLR_STORED_PROCEDURE" => "Stored procedure",
        "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION"
            or "CLR_SCALAR_FUNCTION" or "CLR_TABLE_VALUED_FUNCTION" or "AGGREGATE_FUNCTION" => "Function",
        "SQL_TRIGGER" or "CLR_TRIGGER" => "Trigger",
        "SYNONYM" => "Synonym",
        "SEQUENCE_OBJECT" => "Sequence",
        "OBSYNC_FK_TABLE" => "Table (foreign key)",
        "OBSYNC_CROSS_DB" => "Cross-database reference",
        "OBSYNC_UNRESOLVED" => "Unresolved reference",
        _ => Humanize(typeDesc),
    };

    // "SERVICE_QUEUE" -> "Service queue" for type_desc values without an explicit mapping.
    private static string Humanize(string typeDesc)
    {
        var words = typeDesc.Replace('_', ' ').ToLowerInvariant();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
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
