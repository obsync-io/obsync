using System.Collections.Immutable;

namespace Obsync.Shared.Objects;

/// <summary>
/// The authoritative catalog of supported SQL Server object types. Drives repository
/// folder layout, the metadata/SMO routing decision, and the redeploy ordering used when
/// scripting a full schema.
/// </summary>
public static class SqlObjectTypeCatalog
{
    private static readonly ImmutableArray<SqlObjectTypeDescriptor> _all =
    [
        //                    Type                              DisplayName                  FolderName                            SchemaScoped Strategy                  Order
        new(SqlObjectType.User,                 "Users",                     "security/users",                     false,       ScriptingStrategy.Smo,      1),
        new(SqlObjectType.Role,                 "Roles",                     "security/roles",                     false,       ScriptingStrategy.Smo,      2),
        new(SqlObjectType.ApplicationRole,      "Application Roles",         "security/application-roles",         false,       ScriptingStrategy.Smo,      3),
        new(SqlObjectType.Schema,               "Schemas",                   "schemas",                            false,       ScriptingStrategy.Metadata, 4),
        new(SqlObjectType.UserDefinedDataType,  "User-Defined Data Types",   "types/user-defined-data-types",      true,        ScriptingStrategy.Smo,      10),
        new(SqlObjectType.UserDefinedTableType, "User-Defined Table Types",  "types/user-defined-table-types",     true,        ScriptingStrategy.Smo,      11),
        new(SqlObjectType.XmlSchemaCollection,  "XML Schema Collections",    "types/xml-schema-collections",       true,        ScriptingStrategy.Smo,      12),
        new(SqlObjectType.UserDefinedType,      "CLR User-Defined Types",    "types/user-defined-types",           true,        ScriptingStrategy.Smo,      13),
        new(SqlObjectType.UserDefinedAggregate, "CLR Aggregates",            "types/user-defined-aggregates",      true,        ScriptingStrategy.Smo,      14),
        new(SqlObjectType.PartitionFunction,    "Partition Functions",       "storage/partition-functions",        false,       ScriptingStrategy.Smo,      20),
        new(SqlObjectType.PartitionScheme,      "Partition Schemes",         "storage/partition-schemes",          false,       ScriptingStrategy.Smo,      21),
        new(SqlObjectType.Assembly,             "Assemblies",                "assemblies",                         false,       ScriptingStrategy.Smo,      22),
        new(SqlObjectType.FullTextCatalog,      "Full-Text Catalogs",        "full-text-catalogs",                 false,       ScriptingStrategy.Smo,      23),
        new(SqlObjectType.ColumnMasterKey,      "Column Master Keys",        "security/column-master-keys",        false,       ScriptingStrategy.Smo,      30),
        new(SqlObjectType.ColumnEncryptionKey,  "Column Encryption Keys",    "security/column-encryption-keys",    false,       ScriptingStrategy.Smo,      31),
        new(SqlObjectType.Sequence,             "Sequences",                 "sequences",                          true,        ScriptingStrategy.Metadata, 40),
        new(SqlObjectType.Synonym,              "Synonyms",                  "synonyms",                           true,        ScriptingStrategy.Metadata, 41),
        new(SqlObjectType.Table,                "Tables",                    "tables",                             true,        ScriptingStrategy.Smo,      42),
        new(SqlObjectType.View,                 "Views",                     "views",                              true,        ScriptingStrategy.Metadata, 43),
        new(SqlObjectType.Function,             "Functions",                 "functions",                          true,        ScriptingStrategy.Metadata, 44),
        new(SqlObjectType.StoredProcedure,      "Stored Procedures",         "procedures",                         true,        ScriptingStrategy.Metadata, 45),
        new(SqlObjectType.Trigger,              "Triggers",                  "triggers",                           true,        ScriptingStrategy.Metadata, 46),
        new(SqlObjectType.DatabaseDdlTrigger,   "Database DDL Triggers",     "triggers/database",                  false,       ScriptingStrategy.Metadata, 47),
        new(SqlObjectType.SecurityPolicy,       "Security Policies",         "security/policies",                  true,        ScriptingStrategy.Smo,      50),

        // --- Server-scoped (instance-level) objects, versioned under server/ when a job opts in ---
        new(SqlObjectType.ServerLogin,          "Login",                     "server/logins",                      false,       ScriptingStrategy.Smo,      100, IsServerScoped: true),
        new(SqlObjectType.ServerRole,           "Server role",               "server/roles",                       false,       ScriptingStrategy.Smo,      101, IsServerScoped: true),
        new(SqlObjectType.ServerCredential,     "Server credential",         "server/credentials",                 false,       ScriptingStrategy.Smo,      102, IsServerScoped: true),
        new(SqlObjectType.LinkedServer,         "Linked server",             "server/linked-servers",              false,       ScriptingStrategy.Smo,      103, IsServerScoped: true),
        new(SqlObjectType.AgentJob,             "SQL Agent job",             "server/agent/jobs",                  false,       ScriptingStrategy.Smo,      110, IsServerScoped: true),
        new(SqlObjectType.AgentOperator,        "SQL Agent operator",        "server/agent/operators",             false,       ScriptingStrategy.Smo,      111, IsServerScoped: true),
        new(SqlObjectType.AgentAlert,           "SQL Agent alert",           "server/agent/alerts",                false,       ScriptingStrategy.Smo,      112, IsServerScoped: true),
    ];

    private static readonly ImmutableDictionary<SqlObjectType, SqlObjectTypeDescriptor> _byType =
        _all.ToImmutableDictionary(d => d.Type);

    /// <summary>All descriptors, in redeploy order.</summary>
    public static IReadOnlyList<SqlObjectTypeDescriptor> All { get; } =
        [.. _all.OrderBy(d => d.RedeployOrder)];

    /// <summary>Looks up the descriptor for a given object type.</summary>
    public static SqlObjectTypeDescriptor Get(SqlObjectType type) => _byType[type];

    /// <summary>Tries to look up the descriptor for a given object type.</summary>
    public static bool TryGet(SqlObjectType type, out SqlObjectTypeDescriptor descriptor) =>
        _byType.TryGetValue(type, out descriptor!);

    /// <summary>
    /// Orders a set of object types by their clean-rebuild redeploy order
    /// (principals → schemas → types → storage → tables → programmability → policies).
    /// </summary>
    public static IReadOnlyList<SqlObjectType> InRedeployOrder(IEnumerable<SqlObjectType> types) =>
        [.. types.Distinct().OrderBy(t => _byType[t].RedeployOrder)];
}
