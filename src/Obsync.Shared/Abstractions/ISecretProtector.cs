namespace Obsync.Shared.Abstractions;

/// <summary>
/// Encrypts and decrypts small secrets at rest (e.g. for fields that must live inside the
/// local state database). Implemented with Windows DPAPI in <c>Obsync.Security</c>.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a UTF-8 secret and returns an opaque, storable token.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>.</summary>
    string Unprotect(string protectedValue);
}
