using System.Security.Cryptography;
using System.Text;

namespace Obsync.Shared.Scripting;

/// <summary>Computes the content hash used for change detection.</summary>
public interface IObjectHasher
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="content"/>.</summary>
    string ComputeHash(string content);
}

/// <inheritdoc cref="IObjectHasher" />
public sealed class Sha256ObjectHasher : IObjectHasher
{
    public string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
