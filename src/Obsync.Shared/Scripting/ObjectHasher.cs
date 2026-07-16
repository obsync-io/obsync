using System.Security.Cryptography;
using System.Text;

namespace Obsync.Shared.Scripting;

/// <summary>Computes the content hash used for change detection.</summary>
public interface IObjectHasher
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="content"/>.</summary>
    string ComputeHash(string content);

    /// <summary>
    /// Lowercase hex SHA-256 of <paramref name="utf8Content"/>. Identical to
    /// <see cref="ComputeHash(string)"/> of the string those bytes encode — callers that already
    /// hold the UTF-8 bytes (to write them to disk) hash them directly instead of encoding twice.
    /// </summary>
    string ComputeHash(ReadOnlySpan<byte> utf8Content);
}

/// <inheritdoc cref="IObjectHasher" />
public sealed class Sha256ObjectHasher : IObjectHasher
{
    public string ComputeHash(string content) =>
        ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty));

    public string ComputeHash(ReadOnlySpan<byte> utf8Content)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8Content, hash);
        return Convert.ToHexStringLower(hash);
    }
}
