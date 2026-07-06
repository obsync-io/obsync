using System.Collections.Immutable;

namespace Obsync.Shared.Objects;

/// <summary>
/// Expands the high-level <see cref="ObjectSelectionPreset"/> options offered in the Create
/// Job wizard into concrete sets of <see cref="SqlObjectType"/>.
/// </summary>
public static class ObjectSelectionPresets
{
    private static readonly ImmutableArray<SqlObjectType> _programmability =
    [
        SqlObjectType.StoredProcedure,
        SqlObjectType.View,
        SqlObjectType.Function,
        SqlObjectType.Trigger,
    ];

    private static readonly ImmutableArray<SqlObjectType> _recommended =
    [
        SqlObjectType.Schema,
        SqlObjectType.Table,
        SqlObjectType.View,
        SqlObjectType.StoredProcedure,
        SqlObjectType.Function,
        SqlObjectType.Trigger,
        SqlObjectType.Synonym,
        SqlObjectType.Sequence,
        SqlObjectType.User,
        SqlObjectType.Role,
    ];

    /// <summary>
    /// Returns the object types included by a preset, in redeploy order. <see cref="ObjectSelectionPreset.Custom"/>
    /// returns an empty set — callers supply the explicit selection instead.
    /// </summary>
    public static IReadOnlyList<SqlObjectType> Expand(ObjectSelectionPreset preset) => preset switch
    {
        ObjectSelectionPreset.ProgrammabilityOnly => SqlObjectTypeCatalog.InRedeployOrder(_programmability),
        ObjectSelectionPreset.Recommended => SqlObjectTypeCatalog.InRedeployOrder(_recommended),
        // Presets choose DATABASE objects only; server-level types are opted into separately via
        // ObjectSelectionProfile.ServerTypes.
        ObjectSelectionPreset.FullSchema => [.. SqlObjectTypeCatalog.All.Where(d => !d.IsServerScoped).Select(d => d.Type)],
        _ => [],
    };
}
