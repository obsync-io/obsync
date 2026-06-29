namespace Obsync.Shared.Objects;

/// <summary>
/// The SQL Server object types Obsync can script. The set mirrors the coverage of the
/// reference scripting engine so a job can capture a full schema snapshot.
/// </summary>
public enum SqlObjectType
{
    // --- Principals and containers (scripted first on a clean redeploy) ---
    User = 0,
    Role = 1,
    ApplicationRole = 2,
    Schema = 3,

    // --- Types ---
    UserDefinedDataType = 10,
    UserDefinedTableType = 11,
    XmlSchemaCollection = 12,
    UserDefinedType = 13,        // CLR
    UserDefinedAggregate = 14,   // CLR

    // --- Storage / programmable infrastructure ---
    PartitionFunction = 20,
    PartitionScheme = 21,
    Assembly = 22,
    FullTextCatalog = 23,

    // --- Always Encrypted keys ---
    ColumnMasterKey = 30,
    ColumnEncryptionKey = 31,

    // --- Core relational + programmability ---
    Sequence = 40,
    Synonym = 41,
    Table = 42,
    View = 43,
    Function = 44,            // T-SQL scalar/inline/table-valued and CLR functions
    StoredProcedure = 45,
    Trigger = 46,            // DML triggers on tables/views
    DatabaseDdlTrigger = 47, // database-scoped DDL triggers

    // --- Row-level security (scripted last: binds predicate functions to tables) ---
    SecurityPolicy = 50,
}
