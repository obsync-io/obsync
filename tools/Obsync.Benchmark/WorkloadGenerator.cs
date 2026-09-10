using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Obsync.Benchmark;

/// <summary>
/// Creates a benchmark database with a configurable number of generated objects under a
/// <c>bench</c> schema: stored procedures, views, scalar functions, and tables, plus a small set of
/// deliberately hostile objects (encrypted, path-unfriendly names) so failure and path handling are
/// exercised at scale.
/// </summary>
/// <remarks>
/// Two generation modes, and the difference matters for what a report may claim:
/// <list type="bullet">
/// <item><b>Top-up</b> (default) only creates what is missing. It never removes anything and never
/// resizes an object that already exists, so a run asking for 10,000 objects against a database
/// that already holds 50,000 measures 50,000 — the request is a floor, not the workload.</item>
/// <item><b>Reset</b> (<c>--reset-workload</c>) drops every generated object first, so the database
/// afterwards holds EXACTLY the requested counts at the requested body size.</item>
/// </list>
/// Either way <see cref="MeasureAsync"/> reads back what is actually in the database, and that —
/// not the request — is what the benchmark report prints.
/// </remarks>
public sealed class WorkloadGenerator(string server, string database, int bodyCharacters)
{
    private const int BatchSize = 100;

    /// <summary>Statements dropped per round trip when resetting; keeps each batch small enough to plan quickly.</summary>
    private const int DropBatchSize = 500;

    public async Task<GeneratedWorkload> EnsureAsync(
        int procs, int views, int functions, int tables, bool includeHostileObjects, bool reset)
    {
        await using (var master = Open("master"))
        {
            await master.OpenAsync();
            await Exec(master, $"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];");
        }

        await using var db = Open(database);
        await db.OpenAsync();
        await Exec(db, "IF SCHEMA_ID(N'bench') IS NULL EXEC(N'CREATE SCHEMA bench');");

        var dropped = 0;
        if (reset)
        {
            dropped = await ResetAsync(db);
        }

        await Exec(db, """
            IF OBJECT_ID(N'bench.base') IS NULL
            CREATE TABLE bench.base (
                id INT NOT NULL PRIMARY KEY,
                payload NVARCHAR(200) NOT NULL,
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
            """);

        var created = 0;
        created += await TopUpAsync(db, "P", "p_", procs, ProcDefinition);
        created += await TopUpAsync(db, "V", "v_", views, ViewDefinition);
        created += await TopUpAsync(db, "FN", "fn_", functions, FunctionDefinition);
        created += await TopUpTablesAsync(db, tables);

        if (includeHostileObjects)
        {
            created += await EnsureHostileObjectsAsync(db);
        }

        return new GeneratedWorkload(procs + views + functions + tables, created, dropped);
    }

    /// <summary>
    /// Reads back what the database actually holds. This is the only workload figure a report may
    /// print as measured — the requested counts describe an intention, not a scan.
    /// </summary>
    public async Task<WorkloadInventory> MeasureAsync()
    {
        await using var db = Open(database);
        await db.OpenAsync();

        await using var cmd = db.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = """
            SELECT
                ISNULL(SUM(CASE WHEN type = 'P' THEN 1 ELSE 0 END), 0) AS Procedures,
                ISNULL(SUM(CASE WHEN type = 'V' THEN 1 ELSE 0 END), 0) AS Views,
                ISNULL(SUM(CASE WHEN type IN ('FN', 'IF', 'TF') THEN 1 ELSE 0 END), 0) AS Functions,
                ISNULL(SUM(CASE WHEN type = 'U' THEN 1 ELSE 0 END), 0) AS Tables
            FROM sys.objects
            WHERE is_ms_shipped = 0 AND type IN ('P', 'V', 'FN', 'IF', 'TF', 'U');

            SELECT COUNT(*) FROM sys.schemas
            WHERE name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'guest', N'db_owner', N'db_accessadmin',
                               N'db_securityadmin', N'db_ddladmin', N'db_backupoperator', N'db_datareader',
                               N'db_datawriter', N'db_denydatareader', N'db_denydatawriter');

            -- Encrypted modules have a NULL definition; they are counted as modules but contribute
            -- no characters, which is exactly how they reach the engine too.
            SELECT
                COUNT(*) AS Modules,
                ISNULL(SUM(CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END), 0) AS WithoutDefinition,
                ISNULL(SUM(CAST(DATALENGTH(m.definition) / 2 AS BIGINT)), CAST(0 AS BIGINT)) AS TotalChars,
                ISNULL(MAX(CAST(DATALENGTH(m.definition) / 2 AS BIGINT)), CAST(0 AS BIGINT)) AS MaxChars
            FROM sys.sql_modules AS m
            INNER JOIN sys.objects AS o ON o.object_id = m.object_id
            WHERE o.is_ms_shipped = 0;
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var procedures = reader.GetInt32(0);
        var views = reader.GetInt32(1);
        var functions = reader.GetInt32(2);
        var tables = reader.GetInt32(3);

        await reader.NextResultAsync();
        await reader.ReadAsync();
        var schemas = reader.GetInt32(0);

        await reader.NextResultAsync();
        await reader.ReadAsync();
        var modules = reader.GetInt32(0);
        var modulesWithoutDefinition = reader.GetInt32(1);
        var totalChars = reader.GetInt64(2);
        var maxChars = reader.GetInt64(3);

        return new WorkloadInventory(
            procedures, views, functions, tables, schemas,
            modules, modulesWithoutDefinition, totalChars, maxChars);
    }

    /// <summary>ALTERs the first <paramref name="count"/> procedures so their definitions change.</summary>
    public async Task TouchProceduresAsync(int count)
    {
        await using var db = Open(database);
        await db.OpenAsync();
        var revision = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        for (var start = 1; start <= count; start += BatchSize)
        {
            var last = Math.Min(count, start + BatchSize - 1);
            var definitions = new List<string>(last - start + 1);
            for (var i = start; i <= last; i++)
            {
                definitions.Add(ProcDefinition(i, $"rev {revision}"));
            }

            // Only ALTER the ones that exist: a --touch larger than the generated procedure count
            // would otherwise CREATE new objects and silently change the workload mid-suite.
            await ExecDefinitions(db, definitions, guardPrefix: "bench.p_", firstIndex: start);
        }
    }

    private async Task<int> TopUpAsync(
        SqlConnection db, string typeCode, string prefix, int target, Func<int, string, string> definition)
    {
        var existing = await Count(db, typeCode, prefix);
        var created = 0;
        for (var start = existing + 1; start <= target; start += BatchSize)
        {
            var last = Math.Min(target, start + BatchSize - 1);
            var definitions = new List<string>(last - start + 1);
            for (var i = start; i <= last; i++)
            {
                definitions.Add(definition(i, "rev 1"));
                created++;
            }

            await ExecDefinitions(db, definitions, guardPrefix: null, firstIndex: start);
        }

        return created;
    }

    private static async Task<int> TopUpTablesAsync(SqlConnection db, int target)
    {
        var existing = await Count(db, "U", "t_");
        var created = 0;
        for (var start = existing + 1; start <= target; start += BatchSize)
        {
            var sb = new StringBuilder();
            for (var i = start; i <= Math.Min(target, start + BatchSize - 1); i++)
            {
                sb.AppendLine($"""
                    IF OBJECT_ID(N'bench.t_{i:D6}') IS NULL
                    CREATE TABLE bench.t_{i:D6} (
                        id INT NOT NULL CONSTRAINT PK_bench_t_{i:D6} PRIMARY KEY,
                        name NVARCHAR(100) NOT NULL,
                        amount DECIMAL(18,2) NULL,
                        created DATETIME2 NOT NULL CONSTRAINT DF_bench_t_{i:D6} DEFAULT SYSUTCDATETIME());
                    """);
                created++;
            }

            await Exec(db, sb.ToString());
        }

        return created;
    }

    /// <summary>
    /// Drops every object under the <c>bench</c> schema so the next generation pass produces exactly
    /// the requested workload at the requested body size. Modules go first (they reference the
    /// tables), then the tables — <c>bench.base</c> included; <see cref="EnsureAsync"/> recreates it.
    /// </summary>
    private static async Task<int> ResetAsync(SqlConnection db)
    {
        var dropped = 0;
        dropped += await DropAllAsync(db, """
            SELECT TOP (@batch)
                N'DROP ' + CASE o.type
                               WHEN 'P' THEN N'PROCEDURE'
                               WHEN 'V' THEN N'VIEW'
                               ELSE N'FUNCTION'
                           END + N' bench.' + QUOTENAME(o.name) + N';' + CHAR(10)
            FROM sys.objects AS o
            WHERE o.schema_id = SCHEMA_ID(N'bench') AND o.type IN ('P', 'V', 'FN', 'IF', 'TF')
            ORDER BY o.object_id
            """);
        dropped += await DropAllAsync(db, """
            SELECT TOP (@batch) N'DROP TABLE bench.' + QUOTENAME(o.name) + N';' + CHAR(10)
            FROM sys.objects AS o
            WHERE o.schema_id = SCHEMA_ID(N'bench') AND o.type = 'U'
            ORDER BY o.object_id
            """);
        return dropped;
    }

    private static async Task<int> DropAllAsync(SqlConnection db, string selectStatements)
    {
        var dropped = 0;
        while (true)
        {
            List<string> statements = [];
            await using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = selectStatements;
                cmd.CommandTimeout = 600;
                cmd.Parameters.AddWithValue("@batch", DropBatchSize);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    statements.Add(reader.GetString(0));
                }
            }

            if (statements.Count == 0)
            {
                return dropped;
            }

            await Exec(db, string.Concat(statements));
            dropped += statements.Count;
        }
    }

    /// <summary>
    /// Objects that historically break scripting tools: an encrypted procedure (unscriptable — must
    /// surface as a reported skip, not a run failure) and names containing characters that are
    /// illegal or awkward in Windows paths (must map to safe file names, not crash or collide).
    /// </summary>
    private static async Task<int> EnsureHostileObjectsAsync(SqlConnection db)
    {
        string[] statements =
        [
            "IF OBJECT_ID(N'bench.p_encrypted') IS NULL EXEC(N'CREATE PROCEDURE bench.p_encrypted WITH ENCRYPTION AS SELECT 1 AS x;')",
            "IF OBJECT_ID(N'bench.[p colon:name]') IS NULL EXEC(N'CREATE PROCEDURE bench.[p colon:name] AS SELECT 1 AS x;')",
            "IF OBJECT_ID(N'bench.[p star*name]') IS NULL EXEC(N'CREATE PROCEDURE bench.[p star*name] AS SELECT 1 AS x;')",
            "IF OBJECT_ID(N'bench.[p \"quoted\" name]') IS NULL EXEC(N'CREATE PROCEDURE bench.[p \"quoted\" name] AS SELECT 1 AS x;')",
            "IF OBJECT_ID(N'bench.[pünïcode]') IS NULL EXEC(N'CREATE PROCEDURE bench.[pünïcode] AS SELECT 1 AS x;')",
            "IF OBJECT_ID(N'bench.[p.dotted.name]') IS NULL EXEC(N'CREATE PROCEDURE bench.[p.dotted.name] AS SELECT 1 AS x;')",
        ];

        var created = 0;
        foreach (var statement in statements)
        {
            created += await Exec(db, statement) >= 0 ? 1 : 0;
        }

        return created;
    }

    private string ProcDefinition(int i, string revision)
    {
        var head = $"""
            CREATE OR ALTER PROCEDURE bench.p_{i:D6}
                @id INT,
                @take INT = 50
            AS
            BEGIN
                SET NOCOUNT ON;
                -- Obsync benchmark object {i} ({revision}).
                -- The filler below pads the definition to the configured body size so hashing,
                -- normalization, file writes, and git behave like they would on production code.

            """;
        var tail = $"""
                SELECT TOP (@take) b.id, b.payload, b.created_at
                FROM bench.base AS b
                WHERE b.id >= @id AND b.id < @id + {i % 977 + 25}
                ORDER BY b.id;
            END
            """;
        return Pad(head, tail, i);
    }

    private string ViewDefinition(int i, string revision)
    {
        var head = $"""
            CREATE OR ALTER VIEW bench.v_{i:D6}
            AS
            -- Obsync benchmark view {i} ({revision})

            """;
        var tail = $"""
            SELECT b.id, b.payload, b.created_at, b.id % {i % 89 + 2} AS bucket_{i:D6}
            FROM bench.base AS b
            """;
        return Pad(head, tail, i);
    }

    private string FunctionDefinition(int i, string revision)
    {
        var head = $"""
            CREATE OR ALTER FUNCTION bench.fn_{i:D6}(@x INT)
            RETURNS INT
            AS
            BEGIN
                -- Obsync benchmark function {i} ({revision})

            """;
        var tail = $"""
                RETURN (@x * {i % 31 + 2}) + {i % 7};
            END
            """;
        return Pad(head, tail, i);
    }

    /// <summary>
    /// Grows a definition to the configured body size with comment filler. Real stored procedures
    /// run to several kilobytes; a 750-character object measures a pipeline nobody runs, and every
    /// working-tree and repository figure extrapolated from one is optimistic by the same factor.
    /// </summary>
    private string Pad(string head, string tail, int i)
    {
        var body = new StringBuilder(Math.Max(bodyCharacters, head.Length + tail.Length));
        body.Append(head);
        // The same line ending the literals above carry, so a definition never mixes the two: the
        // engine's normalizer converts CRLF to LF before hashing, and that conversion is itself part
        // of what a run is being timed on.
        var newLine = Environment.NewLine;
        var line = 1;
        while (body.Length + tail.Length + newLine.Length < bodyCharacters)
        {
            body.Append("    -- filler line ").Append(line)
                .Append(" of benchmark object ").Append(i)
                .Append(" - padding this definition to the configured body size.")
                .Append(newLine);
            line++;
        }

        body.Append(tail);
        return body.ToString();
    }

    private static async Task<int> Count(SqlConnection db, string typeCode, string prefix)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID(N'bench') AND type = @t AND name LIKE @p + '%'";
        cmd.Parameters.AddWithValue("@t", typeCode);
        cmd.Parameters.AddWithValue("@p", prefix);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Runs a batch of CREATE OR ALTER definitions, each passed as an <c>NVARCHAR(MAX)</c> parameter
    /// rather than inlined into the batch text.
    /// </summary>
    /// <remarks>
    /// Parameters, not string literals, because the body size is configurable: a Unicode literal
    /// longer than 4,000 characters is not reliably usable as an <c>EXEC(N'…')</c> argument, and
    /// escaping multi-kilobyte definitions into the batch text is a second way to get it wrong.
    /// When <paramref name="guardPrefix"/> is set, each statement is wrapped in an existence check
    /// so the batch only ALTERs objects that are already there.
    /// </remarks>
    private static async Task ExecDefinitions(
        SqlConnection db, IReadOnlyList<string> definitions, string? guardPrefix, int firstIndex)
    {
        if (definitions.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder("DECLARE @s NVARCHAR(MAX);\n");
        await using var cmd = db.CreateCommand();
        cmd.CommandTimeout = 600;
        for (var n = 0; n < definitions.Count; n++)
        {
            sb.Append("SET @s = @p").Append(n).Append(";\n");
            if (guardPrefix is not null)
            {
                sb.Append("IF OBJECT_ID(N'").Append(guardPrefix)
                  .Append((firstIndex + n).ToString("D6", CultureInfo.InvariantCulture))
                  .Append("') IS NOT NULL ");
            }

            sb.Append("EXEC sp_executesql @s;\n");
            cmd.Parameters.Add($"@p{n}", SqlDbType.NVarChar, -1).Value = definitions[n];
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> Exec(SqlConnection db, string sql)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        return await cmd.ExecuteNonQueryAsync();
    }

    private SqlConnection Open(string db) => new(
        $"Server={server};Database={db};Integrated Security=SSPI;TrustServerCertificate=True;Connect Timeout=30");
}

/// <summary>What one generation pass asked for and what it changed.</summary>
public sealed record GeneratedWorkload(int TargetObjects, int NewlyCreated, int Dropped);

/// <summary>
/// What the benchmark database actually holds, read back from SQL Server after generation. The
/// report prints these — never the requested counts — because top-up generation leaves whatever a
/// previous, larger run created in place.
/// </summary>
public sealed record WorkloadInventory(
    int Procedures, int Views, int Functions, int Tables, int Schemas,
    int Modules, int ModulesWithoutDefinition, long TotalModuleChars, long MaxModuleChars)
{
    /// <summary>Objects of the types the benchmark job selects (schemas included).</summary>
    public int TotalObjects => Procedures + Views + Functions + Tables + Schemas;

    public int ModulesWithDefinition => Modules - ModulesWithoutDefinition;

    public double AverageModuleChars =>
        ModulesWithDefinition > 0 ? (double)TotalModuleChars / ModulesWithDefinition : 0;
}
