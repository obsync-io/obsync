using Microsoft.Data.SqlClient;

namespace Obsync.E2E;

/// <summary>
/// Creates and mutates the disposable audit databases. Everything here is DDL against
/// dedicated E2E databases — never against any pre-existing database.
/// </summary>
internal sealed class SqlSeeder(string server)
{
    public const string MainDb = "ObsyncAuditE2E";
    public const string SecondDb = "ObsyncAuditE2E2";

    private string MasterConnectionString => new SqlConnectionStringBuilder
    {
        DataSource = server,
        InitialCatalog = "master",
        IntegratedSecurity = true,
        TrustServerCertificate = true,
    }.ConnectionString;

    private string DbConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
    }.ConnectionString;

    public async Task RecreateAsync()
    {
        await ExecAsync(MasterConnectionString,
            $"""
             IF DB_ID('{MainDb}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{MainDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{MainDb}];
             END
             """,
            $"CREATE DATABASE [{MainDb}]",
            $"""
             IF DB_ID('{SecondDb}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{SecondDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{SecondDb}];
             END
             """,
            $"CREATE DATABASE [{SecondDb}]");

        await ExecAsync(DbConnectionString(MainDb),
            // --- Schemas (incl. one that only holds the encrypted module added later) -----------
            "CREATE SCHEMA sales",
            "CREATE SCHEMA hr",
            "CREATE SCHEMA vault",

            // --- Tables: identity, defaults, computed, checks, FKs, unique, indexes -------------
            """
            CREATE TABLE sales.Customers (
                Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                Name NVARCHAR(200) NOT NULL,
                Email NVARCHAR(320) NULL CONSTRAINT UQ_Customers_Email UNIQUE,
                CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT SYSUTCDATETIME()
            )
            """,
            """
            CREATE TABLE sales.Orders (
                Id INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
                CustomerId INT NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES sales.Customers(Id),
                Quantity INT NOT NULL CONSTRAINT CK_Orders_Quantity CHECK (Quantity > 0),
                Price DECIMAL(18,2) NOT NULL CONSTRAINT DF_Orders_Price DEFAULT 0,
                Total AS (Quantity * Price) PERSISTED,
                [Order] NVARCHAR(50) NULL
            )
            """,
            "CREATE NONCLUSTERED INDEX IX_Orders_Customer ON sales.Orders(CustomerId) INCLUDE (Price)",
            "CREATE NONCLUSTERED INDEX IX_Orders_Bulk ON sales.Orders(Quantity) WHERE Quantity > 100",
            """
            CREATE TABLE hr.[Employee Data] (
                Id INT NOT NULL CONSTRAINT [PK_Employee Data] PRIMARY KEY,
                FullName NVARCHAR(100) COLLATE Latin1_General_CS_AS NOT NULL
            )
            """,
            "CREATE TABLE dbo.[Weird]]Name] (Id INT NOT NULL CONSTRAINT [PK_Weird]]Name] PRIMARY KEY)",
            """
            CREATE TABLE dbo.ThisIsAVeryLongTableNameDesignedToExerciseFileNameMappingBehaviorInTheEnginePathMapperWithWellOverOneHundredCharactersOfLength (
                Id INT NOT NULL PRIMARY KEY
            )
            """,

            // --- Reference data tables ----------------------------------------------------------
            """
            CREATE TABLE dbo.RefStatus (
                Code INT NOT NULL CONSTRAINT PK_RefStatus PRIMARY KEY,
                Label NVARCHAR(50) NOT NULL
            )
            """,
            """
            INSERT INTO dbo.RefStatus (Code, Label) VALUES
                (1, N'Active'), (2, N'O''Brien'), (3, N'Ünïcødé ✓'), (4, N'Closed')
            """,
            """
            CREATE TABLE dbo.RefBig (
                Id INT NOT NULL CONSTRAINT PK_RefBig PRIMARY KEY,
                Payload NVARCHAR(20) NOT NULL
            )
            """,
            """
            INSERT INTO dbo.RefBig (Id, Payload)
            SELECT TOP (6000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'row'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b
            """,

            // --- Views (plain + indexed) --------------------------------------------------------
            """
            CREATE VIEW sales.vwOrders AS
                SELECT o.Id, c.Name, o.Quantity, o.Price, o.Total
                FROM sales.Orders o JOIN sales.Customers c ON c.Id = o.CustomerId
            """,
            """
            CREATE VIEW dbo.vwOrderTotals WITH SCHEMABINDING AS
                SELECT o.CustomerId, COUNT_BIG(*) AS OrderCount, SUM(ISNULL(o.Total, 0)) AS GrandTotal
                FROM sales.Orders o GROUP BY o.CustomerId
            """,
            "CREATE UNIQUE CLUSTERED INDEX IX_vwOrderTotals ON dbo.vwOrderTotals(CustomerId)",
            "CREATE VIEW dbo.vwToDelete AS SELECT 1 AS One",

            // --- Procedures (params, reserved words, spaces, unicode) ---------------------------
            """
            CREATE PROCEDURE sales.usp_GetOrders
                @From DATETIME2, @Select NVARCHAR(10) = N'all'
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT * FROM sales.vwOrders;
            END
            """,
            "CREATE PROCEDURE dbo.[usp with space] AS SELECT 1",
            "CREATE PROCEDURE dbo.[uspÜnïcødé] AS SELECT N'Ünïcødé'",
            "CREATE PROCEDURE dbo.usp_RenameMe AS SELECT 42",

            // --- Functions: scalar, inline TVF, multi-statement TVF -----------------------------
            """
            CREATE FUNCTION dbo.fnScalar(@x INT) RETURNS INT
            AS BEGIN RETURN @x * 2 END
            """,
            """
            CREATE FUNCTION dbo.fnInlineTvf() RETURNS TABLE
            AS RETURN (SELECT Code, Label FROM dbo.RefStatus)
            """,
            """
            CREATE FUNCTION dbo.fnMultiTvf() RETURNS @t TABLE (Id INT)
            AS BEGIN INSERT INTO @t VALUES (1); RETURN; END
            """,

            // --- Trigger, sequence, synonym, UDTs, XML schema collection ------------------------
            """
            CREATE TRIGGER sales.trgOrders ON sales.Orders AFTER INSERT
            AS SET NOCOUNT ON;
            """,
            "CREATE SEQUENCE dbo.seqOrder AS INT START WITH 1 INCREMENT BY 1",
            "CREATE SYNONYM dbo.synCustomers FOR sales.Customers",
            "CREATE TYPE dbo.PhoneNumber FROM VARCHAR(20) NOT NULL",
            "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL PRIMARY KEY)",
            """
            CREATE XML SCHEMA COLLECTION dbo.OrderXml AS
            N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:e2e"
                xmlns="urn:e2e" elementFormDefault="qualified">
                <xs:element name="order" type="xs:string" />
            </xs:schema>'
            """,

            // --- Security: role, user without login, grants -------------------------------------
            "CREATE ROLE app_readers",
            "CREATE USER svc_reporting WITHOUT LOGIN",
            "ALTER ROLE app_readers ADD MEMBER svc_reporting",
            "GRANT SELECT ON SCHEMA::sales TO app_readers",
            "GRANT EXECUTE ON dbo.fnScalar TO svc_reporting",

            // --- Extended properties -------------------------------------------------------------
            """
            EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'All customer orders.',
                @level0type = N'SCHEMA', @level0name = N'sales',
                @level1type = N'TABLE', @level1name = N'Orders'
            """,
            """
            EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Computed line total.',
                @level0type = N'SCHEMA', @level0name = N'sales',
                @level1type = N'TABLE', @level1name = N'Orders',
                @level2type = N'COLUMN', @level2name = N'Total'
            """);

        await ExecAsync(DbConnectionString(SecondDb),
            "CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL)",
            "CREATE PROCEDURE dbo.usp_Widgets AS SELECT * FROM dbo.Widgets");
    }

    public Task ModifyProcAsync() => ExecAsync(DbConnectionString(MainDb),
        """
        ALTER PROCEDURE sales.usp_GetOrders
            @From DATETIME2, @Select NVARCHAR(10) = N'all'
        AS
        BEGIN
            SET NOCOUNT ON;
            -- E2E modification marker
            SELECT * FROM sales.vwOrders WHERE 1 = 1;
        END
        """);

    public Task ModifyProcAgainAsync() => ExecAsync(DbConnectionString(MainDb),
        """
        ALTER PROCEDURE sales.usp_GetOrders
            @From DATETIME2, @Select NVARCHAR(10) = N'all'
        AS
        BEGIN
            SET NOCOUNT ON;
            -- E2E modification marker two
            SELECT * FROM sales.vwOrders WHERE 2 = 2;
        END
        """);

    public Task DropViewAsync() => ExecAsync(DbConnectionString(MainDb), "DROP VIEW dbo.vwToDelete");

    public Task RenameProcAsync() => ExecAsync(DbConnectionString(MainDb),
        "EXEC sp_rename 'dbo.usp_RenameMe', 'usp_Renamed'");

    public Task AddEncryptedProcAsync() => ExecAsync(DbConnectionString(MainDb),
        "CREATE PROCEDURE vault.usp_Secret WITH ENCRYPTION AS SELECT 'secret'");

    public Task DropDatabasesAsync() => ExecAsync(MasterConnectionString,
        $"""
         IF DB_ID('{MainDb}') IS NOT NULL
         BEGIN
             ALTER DATABASE [{MainDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
             DROP DATABASE [{MainDb}];
         END
         """,
        $"""
         IF DB_ID('{SecondDb}') IS NOT NULL
         BEGIN
             ALTER DATABASE [{SecondDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
             DROP DATABASE [{SecondDb}];
         END
         """);

    public async Task<List<string>> ListUserDatabasesAsync()
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task ExecAsync(string connectionString, params string[] batches)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }
}
