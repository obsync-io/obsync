namespace Obsync.Shared.Objects;

/// <summary>
/// Resolves a user-typed object-type token (e.g. <c>table</c>, <c>stored procedure</c>, <c>proc</c>) to
/// a <see cref="SqlObjectType"/>, for the <c>types:</c> section of a <c>.obsyncignore</c> file.
/// </summary>
public static class SqlObjectTypeAliases
{
    private static readonly Dictionary<string, SqlObjectType> Map = Build();

    public static bool TryResolve(string token, out SqlObjectType type) =>
        Map.TryGetValue(Normalize(token), out type);

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static Dictionary<string, SqlObjectType> Build()
    {
        var map = new Dictionary<string, SqlObjectType>();

        void Add(string alias, SqlObjectType type)
        {
            var key = Normalize(alias);
            if (key.Length > 0)
            {
                map[key] = type;
            }
        }

        foreach (var descriptor in SqlObjectTypeCatalog.All)
        {
            Add(descriptor.Type.ToString(), descriptor.Type);           // enum name (singular, e.g. StoredProcedure)
            Add(descriptor.DisplayName, descriptor.Type);               // plural display (e.g. "Stored Procedures")
            Add(descriptor.DisplayName.TrimEnd('s'), descriptor.Type);  // rough singular
        }

        // Common short aliases.
        Add("proc", SqlObjectType.StoredProcedure);
        Add("procs", SqlObjectType.StoredProcedure);
        Add("sproc", SqlObjectType.StoredProcedure);
        Add("sp", SqlObjectType.StoredProcedure);
        Add("func", SqlObjectType.Function);
        Add("udf", SqlObjectType.Function);
        Add("tbl", SqlObjectType.Table);
        Add("vw", SqlObjectType.View);

        return map;
    }
}
