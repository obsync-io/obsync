namespace Obsync.Shared.Models;

/// <summary>
/// The git author/committer identity stamped on sync commits. Configurable in Settings so
/// <c>git blame</c> reflects the owning team; the default keeps the historical "Obsync" author.
/// A null email falls back to the engine's configured default.
/// </summary>
public sealed record CommitterIdentity
{
    public CommitterIdentity()
    {
    }

    public CommitterIdentity(string name, string? email)
    {
        Name = name;
        Email = email;
    }

    public string Name { get; init; } = "Obsync";
    public string? Email { get; init; }

    public static CommitterIdentity Default { get; } = new();
}
