using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Obsync.Shared.Abstractions;

namespace Obsync.Security;

/// <summary>
/// Stores secrets in Windows Credential Manager as generic credentials. Secrets never touch the
/// local state database, configuration files, or logs.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public void Store(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(secret);

        var blob = Encoding.UTF8.GetBytes(secret);
        var targetPtr = Marshal.StringToHGlobalUni(key);
        var userPtr = Marshal.StringToHGlobalUni("Obsync");
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };

            if (!CredWrite(in credential, 0))
            {
                throw Failure(Marshal.GetLastWin32Error(), "store", key);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    public string? Retrieve(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!CredRead(key, CredTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw Failure(error, "read", key);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void Delete(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!CredDelete(key, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw Failure(error, "delete", key);
            }
        }
    }

    public bool Exists(string key) => Retrieve(key) is not null;

    public IReadOnlyList<string> Enumerate(string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);

        // CredEnumerate's own filter is a wildcard match on the target name, but it is documented as
        // matching only a TRAILING wildcard and it behaves differently again when the caller has the
        // per-session enumerate flag. Enumerating everything and filtering here is both simpler and
        // exact — a machine holds tens of credentials, not thousands.
        if (!CredEnumerate(null, 0, out var count, out var arrayPtr))
        {
            var error = Marshal.GetLastWin32Error();

            // An empty vault reports ERROR_NOT_FOUND rather than zero entries.
            return error == ErrorNotFound ? [] : throw Failure(error, "enumerate", keyPrefix);
        }

        try
        {
            var keys = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var entryPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                var entry = Marshal.PtrToStructure<Credential>(entryPtr);
                if (entry.Type != CredTypeGeneric || entry.TargetName == IntPtr.Zero)
                {
                    continue;
                }

                var target = Marshal.PtrToStringUni(entry.TargetName);
                if (target is not null && target.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(target);
                }
            }

            return keys;
        }
        finally
        {
            CredFree(arrayPtr);
        }
    }

    /// <summary>
    /// Builds the exception for a failed Credential Manager call, keeping BOTH the Windows
    /// description and the numeric code.
    /// </summary>
    /// <remarks>
    /// The two-argument <see cref="Win32Exception(int, string)"/> constructor REPLACES
    /// <see cref="Exception.Message"/> with the supplied text, so the OS description was destroyed
    /// and <see cref="Win32Exception.NativeErrorCode"/> never reached any user-facing surface. The
    /// result was a message with neither a cause nor a code — "Failed to read credential
    /// 'Obsync:GitHub:…'" — which is exactly the class of failure a support engineer needs the code
    /// for. ERROR_NO_SUCH_LOGON_SESSION (1312), the ordinary result for a service account with no
    /// loaded profile, reads completely differently once its description survives.
    /// </remarks>
    private static Win32Exception Failure(int error, string operation, string key)
    {
        // The one-argument constructor is what resolves the description from the OS.
        var description = new Win32Exception(error).Message;
        return new Win32Exception(
            error, $"Windows Credential Manager could not {operation} '{key}': {description} (error {error}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentialsPtr);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
