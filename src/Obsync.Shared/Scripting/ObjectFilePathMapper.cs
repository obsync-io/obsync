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
    /// <c>procedures/dbo.usp_GetCustomer.sql</c>. Deterministic across runs, and distinct for two
    /// objects whose names differ by anything more than letter case.
    /// </summary>
    /// <remarks>
    /// Two names differing ONLY by case are the one exception, and it cannot be resolved here: the
    /// mapper sees a single identity at a time, so it cannot know a twin exists. It returns two
    /// paths that differ only in case, which a case-insensitive filesystem treats as one file. Only
    /// a case-sensitive database collation can produce such a pair, and the engine rejects it up
    /// front — see <c>SyncEngine.GuardAgainstCaseTwin</c>.
    /// </remarks>
    string MapRelativePath(ScriptedObjectIdentity identity);
}

/// <inheritdoc cref="IObjectFilePathMapper" />
public sealed class ObjectFilePathMapper : IObjectFilePathMapper
{
    /// <summary>
    /// The repository layout version: bump when a change here would put an object's file at a
    /// DIFFERENT path than a previous release did — folder names, the sanitizer, the stem cap, the
    /// collision suffix.
    /// </summary>
    /// <remarks>
    /// A layout change is worse than a format change and must invalidate every watermark. Stale-path
    /// cleanup only runs for objects that reach the apply path, so an object the planner skips keeps
    /// its OLD file, and its state row keeps the old path — leaving two files for one object with
    /// nothing to reconcile them. Feeds <see cref="EmissionFingerprint"/>.
    /// </remarks>
    public const int LayoutVersion = 1;

    private const string Extension = ".sql";

    /// <summary>Cap the stem length so even deeply nested workspaces stay within Windows path limits.</summary>
    private const int MaxStemLength = 120;

    private static readonly SearchValues<char> InvalidChars =
        SearchValues.Create(new string(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Win32 device names. A path component that matches one — with or without an extension — is
    /// not a normal file: <c>git add</c> fails outright on <c>CON.sql</c> (exit 128, nothing
    /// staged, so the whole run fails), and a directory named <c>CON</c> makes git skip it with a
    /// warning and exit 0, which reads to the engine as "no changes". Both are reachable from a SQL
    /// object or database simply named <c>CON</c>.
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Turns an arbitrary name into exactly ONE safe path component. Used for names that are not
    /// object identities — notably the database name, which is composed into the repository path
    /// and, unsanitized, let a name like <c>..\..\Windows\Temp</c> redirect writes outside the
    /// workspace. Separators become <c>_</c> here, so the result can never add a path segment.
    /// </summary>
    public static string SanitizePathSegment(string name)
    {
        var segment = Sanitize(name, out var changed);

        if (segment.Length > MaxStemLength)
        {
            segment = segment[..MaxStemLength];
            changed = true;
        }

        // Same rule as an object stem: if sanitizing altered the name, a stable suffix keeps two
        // different names that sanitize alike (".." and ".", or two long shared-prefix names) apart.
        return changed ? $"{segment}_{StableSuffix(name)}" : segment;
    }

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

        if (IsReservedDeviceName(result))
        {
            changed = true;
            return $"_{result}";
        }

        return result;
    }

    /// <summary>
    /// True when a component would be interpreted as a Win32 device. The reservation is decided by
    /// the text before the first '.' (so <c>CON.sql</c> and <c>CON.foo.sql</c> both count), with
    /// trailing spaces ignored — but <c>CONX</c>, <c>CON1</c> and <c>COM0</c> are ordinary names.
    /// </summary>
    private static bool IsReservedDeviceName(string component)
    {
        var dot = component.IndexOf('.');
        var stem = (dot < 0 ? component : component[..dot]).TrimEnd(' ');

        return ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static string StableSuffix(ScriptedObjectIdentity identity) =>
        StableSuffix($"{identity.Type}|{identity.Schema}|{identity.Name}");

    private static string StableSuffix(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
