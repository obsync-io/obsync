using System.Security.Cryptography;
using System.Text;
using Obsync.Shared.Abstractions;

namespace Obsync.Security;

/// <summary>
/// Encrypts small secrets at rest with Windows DPAPI, scoped to the current user. Used for any
/// secret that must live inside a local file; primary secret storage is the Credential Manager.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Extra entropy mixed into the DPAPI blob so Obsync secrets cannot be decrypted by another
    // application running as the same user without also knowing this value.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Obsync.DPAPI.v1");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
