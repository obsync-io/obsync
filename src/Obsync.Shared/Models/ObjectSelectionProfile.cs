using Obsync.Shared.Objects;

namespace Obsync.Shared.Models;

/// <summary>
/// Describes which objects a job scripts and how. Backed by a <see cref="ObjectSelectionPreset"/>
/// for the common cases, with an explicit type set for <see cref="ObjectSelectionPreset.Custom"/>.
/// </summary>
public sealed class ObjectSelectionProfile
{
    public ObjectSelectionPreset Preset { get; set; } = ObjectSelectionPreset.Recommended;

    /// <summary>
    /// The explicit object types selected when <see cref="Preset"/> is
    /// <see cref="ObjectSelectionPreset.Custom"/>. Ignored for other presets.
    /// </summary>
    public HashSet<SqlObjectType> CustomTypes { get; set; } = [];

    /// <summary>
    /// Server-level (instance-scoped) object types this job additionally versions under the
    /// repository's <c>server/</c> tree — logins, Agent jobs, linked servers, etc. Empty means
    /// the feature is off. Independent of <see cref="Preset"/>, which covers database objects only.
    /// </summary>
    public HashSet<SqlObjectType> ServerTypes { get; set; } = [];

    /// <summary>Optional schema allow-list. Empty means all schemas.</summary>
    public HashSet<string> SchemaFilter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Include GRANT/REVOKE permission statements in scripted output.</summary>
    public bool IncludePermissions { get; set; } = true;

    /// <summary>Include extended properties (MS_Description etc.).</summary>
    public bool IncludeExtendedProperties { get; set; } = true;

    /// <summary>Write <c>metadata/object-inventory.json</c>: a manifest of every tracked object and its hash.</summary>
    public bool IncludeObjectInventory { get; set; } = true;

    /// <summary>Write <c>metadata/database-options.sql</c>: the database-level <c>ALTER DATABASE … SET</c> settings.</summary>
    public bool IncludeDatabaseOptions { get; set; } = true;

    /// <summary>Write <c>security/permissions/permissions.sql</c>: consolidated database-scoped GRANT/DENY statements.</summary>
    public bool IncludeDatabasePermissionsFile { get; set; } = true;

    /// <summary>Write <c>docs/README.md</c>: a generated object index + data dictionary (markdown).</summary>
    public bool IncludeDocumentation { get; set; } = true;

    /// <summary>Write <c>security/security-review.md</c>: curated security findings (markdown).</summary>
    public bool IncludeSecurityReview { get; set; } = true;

    /// <summary>Delete files (and commit the deletion) for objects dropped on the source.</summary>
    public bool RemoveDroppedObjects { get; set; } = true;

    /// <summary>Apply script normalization before hashing/writing (recommended for stable diffs).</summary>
    public bool NormalizeScripts { get; set; } = true;

    /// <summary>Glob patterns (matched against <c>schema.name</c>) to exclude from scripting.</summary>
    public List<string> IgnorePatterns { get; set; } = [];

    /// <summary>
    /// Reference/static tables whose DATA is versioned as deterministic INSERT scripts under
    /// <c>data/</c>. Entries are <c>schema.table</c> (a bare name means <c>dbo</c>) and apply to
    /// every database the job scripts; a table missing from a database is reported as a skip.
    /// Tables above <see cref="JobAdvancedOptions.ReferenceDataMaxRows"/> are skipped, never
    /// silently truncated.
    /// </summary>
    public List<string> ReferenceDataTables { get; set; } = [];

    /// <summary>Resolves the effective set of object types in redeploy order.</summary>
    public IReadOnlyList<SqlObjectType> ResolveTypes() =>
        Preset == ObjectSelectionPreset.Custom
            ? SqlObjectTypeCatalog.InRedeployOrder(CustomTypes)
            : ObjectSelectionPresets.Expand(Preset);

    /// <summary>Resolves the selected server-level object types in catalog (redeploy) order.</summary>
    public IReadOnlyList<SqlObjectType> ResolveServerTypes() => SqlObjectTypeCatalog.InRedeployOrder(ServerTypes);
}
