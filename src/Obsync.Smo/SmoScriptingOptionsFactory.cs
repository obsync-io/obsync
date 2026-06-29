using Microsoft.SqlServer.Management.Smo;
using Obsync.Shared.Models;

namespace Obsync.Smo;

/// <summary>
/// Builds the SMO <see cref="ScriptingOptions"/> used for high-fidelity, deterministic,
/// Git-friendly output: no timestamped headers, schema-qualified, and with all table children
/// (keys, constraints, indexes, triggers) scripted inline into the object's own file.
/// </summary>
internal static class SmoScriptingOptionsFactory
{
    public static ScriptingOptions Create(ObjectSelectionProfile selection) => new()
    {
        // Determinism / Git-friendliness.
        IncludeHeaders = false,              // no "Script Date" timestamp -> stable hashes
        IncludeIfNotExists = false,
        ScriptDrops = false,
        IncludeDatabaseContext = false,
        SchemaQualify = true,
        SchemaQualifyForeignKeysReferences = true,
        AnsiPadding = true,

        // Fidelity: emit the full object definition.
        DriAll = true,                       // primary keys, foreign keys, unique, check, defaults
        Indexes = true,
        ClusteredIndexes = true,
        NonClusteredIndexes = true,
        XmlIndexes = true,
        SpatialIndexes = true,
        ColumnStoreIndexes = true,
        FullTextIndexes = true,
        Triggers = true,                     // DML triggers inline in the table file
        NoCollation = false,
        ExtendedProperties = selection.IncludeExtendedProperties,
        Permissions = selection.IncludePermissions,

        // Per-object scripting: no dependency bundling, no data.
        WithDependencies = false,
        ScriptData = false,
    };
}
