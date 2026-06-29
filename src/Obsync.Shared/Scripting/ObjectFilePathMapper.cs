using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Shared.Scripting;

/// <summary>Maps a SQL object to a deterministic, Git-friendly repository path.</summary>
public interface IObjectFilePathMapper
{
    /// <summary>
    /// Returns the database-root-relative path (forward slashes) for an object, e.g.
    /// <c>procedures/dbo.usp_GetCustomer.sql</c>. Deterministic across runs and guaranteed
    /// not to collide for two distinct objects.
    /// </summary>
    string MapRelativePath(ScriptedObjectIdentity identity);
}

/// <inheritdoc cref="IObjectFilePathMapper" />
public sealed class ObjectFilePathMapper : IObjectFilePathMapper
{
    private const string Extension = ".sql";

    /// <summary>Cap the stem length so even deeply nested workspaces stay within Windows path limits.</summary>
    private const int MaxStemLength = 120;

    private static readonly SearchValues<char> InvalidChars =
        SearchValues.Create(new string(Path.GetInvalidFileNameChars()));

    public string MapRelativePath(ScriptedObjectIdentity identity)
    {
        var descriptor = SqlObjectTypeCatalog.Get(identity.Type);

        var baseName = descriptor.IsSchemaScoped && !string.IsNullOrEmpty(identity.Schema)
            ? $"{identity.Schema}.{identity.Name}"
            : identity.Name;

        var stem = Sanitize(baseName, out var changed);

        if (stem.Length > MaxStemLength)
        {
            stem = stem[..MaxStemLength];
            changed = true;
        }

        // A stable, identity-derived suffix is appended whenever sanitization altered the
        // name, guaranteeing uniqueness for two distinct objects that would otherwise map to
        // the same file — without depending on processing order.
        if (changed)
        {
            stem = $"{stem}_{StableSuffix(identity)}";
        }

        return $"{descriptor.FolderName}/{stem}{Extension}";
    }

    private static string Sanitize(string name, out bool changed)
    {
        changed = false;
        if (string.IsNullOrEmpty(name))
        {
            changed = true;
            return "_";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (InvalidChars.Contains(ch))
            {
                builder.Append('_');
                changed = true;
            }
            else
            {
                builder.Append(ch);
            }
        }

        var result = builder.ToString().TrimEnd('.', ' ');
        if (result.Length != builder.Length)
        {
            changed = true;
        }

        if (result.Length == 0)
        {
            changed = true;
            return "_";
        }

        return result;
    }

    private static string StableSuffix(ScriptedObjectIdentity identity)
    {
        var seed = $"{identity.Type}|{identity.Schema}|{identity.Name}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
