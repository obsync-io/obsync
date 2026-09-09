using Microsoft.SqlServer.Management.Smo;

namespace Obsync.Smo;

/// <summary>
/// This assembly's contribution to the emission fingerprint: what SMO was asked to produce, and
/// which SMO produced it.
/// </summary>
/// <remarks>
/// SMO decides the bytes of every table, and it does so from two things Obsync does not fully own —
/// the option set in <c>SmoScriptingOptionsFactory</c>, and the library implementing them. The
/// library version matters on its own: a package bump can change emitted output with no change to
/// this codebase at all, which is precisely the kind of drift a fingerprint exists to catch. It
/// costs one full scan per type on the release that bumps it, which is rare and always deliberate.
/// </remarks>
public static class SmoEmission
{
    /// <summary>
    /// The option-set version. Bump when <c>SmoScriptingOptionsFactory.Create</c> changes in a way
    /// that alters emitted bytes.
    /// </summary>
    /// <remarks>
    /// A version a developer must remember to bump is exactly the discipline that let this gap exist,
    /// so it does not stand alone: <c>SmoEmissionTests</c> pins a digest of the options actually
    /// produced and fails when they change, naming this constant in the failure.
    /// </remarks>
    public const int OptionsVersion = 1;

    private static readonly string LibraryVersion =
        typeof(ScriptingOptions).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>The fingerprint fragment contributed by SMO scripting.</summary>
    public static string Contribution => $"smo-options={OptionsVersion};smo-library={LibraryVersion}";
}
