using System.Text;
using Microsoft.Data.SqlClient;

namespace Obsync.Benchmark;

/// <summary>
/// Creates (or tops up) a benchmark database with a configurable number of generated objects under
/// a <c>bench</c> schema: stored procedures, views, scalar functions, and tables, plus a small set
/// of deliberately hostile objects (encrypted, path-unfriendly names) so failure and path handling
/// are exercised at scale. Generation is idempotent — rerunning with a larger target only creates
/// the missing objects.
/// </summary>
public sealed class WorkloadGenerator(string server, string database)
{
    private const int BatchSize = 100;

    public async Task<GeneratedWorkload> EnsureAsync(int procs, int views, int functions, int tables, bool includeHostileObjects)
    {
        await using (var master = Open("master"))
        {
            await master.OpenAsync();
            await Exec(master, $"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];");
        }

        await using var db = Open(database);
        await db.OpenAsync();
        await Exec(db, "IF SCHEMA_ID(N'bench') IS NULL EXEC(N'CREATE SCHEMA bench');");
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

        return new GeneratedWorkload(procs + views + functions + tables, created);
    }

    /// <summary>ALTERs the first <paramref name="count"/> procedures so their definitions change.</summary>
    public async Task TouchProceduresAsync(int count)
    {
        await using var db = Open(database);
        await db.OpenAsync();
        var revision = DateTime.UtcNow.Ticks;
        for (var start = 1; start <= count; start += BatchSize)
        {
            var sb = new StringBuilder();
            for (var i = start; i <= Math.Min(count, start + BatchSize - 1); i++)
            {
                sb.AppendLine($"IF OBJECT_ID(N'bench.p_{i:D6}') IS NOT NULL EXEC(N'{Escape(ProcDefinition(i, $"rev {revision}"))}');");
            }

            await Exec(db, sb.ToString());
        }
    }

    private async Task<int> TopUpAsync(SqlConnection db, string typeCode, string prefix, int target, Func<int, string, string> definition)
    {
        var existing = await Count(db, typeCode, prefix);
        var created = 0;
        for (var start = existing + 1; start <= target; start += BatchSize)
        {
            var sb = new StringBuilder();
            for (var i = start; i <= Math.Min(target, start + BatchSize - 1); i++)
            {
                sb.AppendLine($"EXEC(N'{Escape(definition(i, "rev 1"))}');");
                created++;
            }

            await Exec(db, sb.ToString());
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

    private static string ProcDefinition(int i, string revision) => $"""
        CREATE OR ALTER PROCEDURE bench.p_{i:D6}
            @id INT,
            @take INT = 50
        AS
        BEGIN
            SET NOCOUNT ON;
            -- Obsync benchmark object {i} ({revision}).
            -- The comment block below pads the definition to a realistic size so hashing,
            -- normalization, file writes, and git behave like they would on production code.
            -- line 1 of filler for object {i}
            -- line 2 of filler for object {i}
            -- line 3 of filler for object {i}
            -- line 4 of filler for object {i}
            -- line 5 of filler for object {i}
            -- line 6 of filler for object {i}
            -- line 7 of filler for object {i}
            -- line 8 of filler for object {i}
            SELECT TOP (@take) b.id, b.payload, b.created_at
            FROM bench.base AS b
            WHERE b.id >= @id AND b.id < @id + {i % 977 + 25}
            ORDER BY b.id;
        END
        """;

    private static string ViewDefinition(int i, string revision) => $"""
        CREATE OR ALTER VIEW bench.v_{i:D6}
        AS
        -- Obsync benchmark view {i} ({revision})
        SELECT b.id, b.payload, b.created_at, b.id % {i % 89 + 2} AS bucket_{i:D6}
        FROM bench.base AS b
        """;

    private static string FunctionDefinition(int i, string revision) => $"""
        CREATE OR ALTER FUNCTION bench.fn_{i:D6}(@x INT)
        RETURNS INT
        AS
        BEGIN
            -- Obsync benchmark function {i} ({revision})
            RETURN (@x * {i % 31 + 2}) + {i % 7};
        END
        """;

    private static async Task<int> Count(SqlConnection db, string typeCode, string prefix)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID(N'bench') AND type = @t AND name LIKE @p + '%'";
        cmd.Parameters.AddWithValue("@t", typeCode);
        cmd.Parameters.AddWithValue("@p", prefix);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<int> Exec(SqlConnection db, string sql)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static string Escape(string sql) => sql.Replace("'", "''");

    private SqlConnection Open(string db) => new(
        $"Server={server};Database={db};Integrated Security=SSPI;TrustServerCertificate=True;Connect Timeout=30");
}

public sealed record GeneratedWorkload(int TargetObjects, int NewlyCreated);
