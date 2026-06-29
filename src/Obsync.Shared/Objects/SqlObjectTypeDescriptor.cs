namespace Obsync.Shared.Objects;

/// <summary>
/// Static metadata about a <see cref="SqlObjectType"/>: how it is named and foldered in
/// the repository, whether it is schema-scoped, and which engine path scripts it.
/// </summary>
/// <param name="Type">The object type this descriptor describes.</param>
/// <param name="DisplayName">Friendly plural name shown in the UI (e.g. "Stored Procedures").</param>
/// <param name="FolderName">Lowercase, Git-friendly folder name (may contain a sub-path such as "security/users").</param>
/// <param name="IsSchemaScoped">True when the object belongs to a schema and its file name is prefixed with the schema.</param>
/// <param name="Strategy">The default engine path used to script this type.</param>
/// <param name="ModuleBased">
/// True when the object's definition lives in <c>sys.sql_modules</c> (procedures, views, functions,
/// triggers) and can be read on the metadata fast path, falling back to SMO when the definition is
/// null (CLR or encrypted).
/// </param>
/// <param name="RedeployOrder">
/// Relative ordering for a clean rebuild on a fresh database. Lower runs first.
/// </param>
public sealed record SqlObjectTypeDescriptor(
    SqlObjectType Type,
    string DisplayName,
    string FolderName,
    bool IsSchemaScoped,
    ScriptingStrategy Strategy,
    bool ModuleBased,
    int RedeployOrder);
