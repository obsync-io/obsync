using System.Security.Cryptography;
using System.Text;
using Obsync.Shared.Models;

namespace Obsync.Shared.Scripting;

/// <summary>
/// A fingerprint of everything that determines the bytes Obsync emits for an object, and where it
/// puts them.
/// </summary>
/// <remarks>
/// Incremental scripting caches a belief: <em>this object has not changed, so do not script it, do
/// not hash it, do not look at its file.</em> That belief silently assumes our own output is stable
/// — that scripting the object again today would produce byte-for-byte what produced the stored
/// hash. Nothing recorded the configuration that produced it, so nothing could notice when the
/// assumption stopped holding.
/// <para>
/// The failure is quiet and permanent. After a release that changes emitted output — or after a user
/// toggles an emission-affecting setting — every object that happens to change later is rewritten in
/// the new format, and every object that does not keeps its old-format file forever. The repository
/// ends up a permanent mixture of both, and the job reports "No changes" throughout. A path-mapper
/// change is worse still: the stale-path cleanup only runs for objects that reach the apply path, so
/// a planner-skipped object keeps its OLD file alongside the new one — a duplicated tree rather than
/// merely a stale one.
/// </para>
/// <para>
/// The inputs are hand-curated rather than derived, which means an input nobody registers is still a
/// gap. <c>EmissionFingerprintTests</c> is what keeps the curation honest: it fails when a setting is
/// added to <see cref="ObjectSelectionProfile"/> without being classified as emission-affecting or
/// explicitly not.
/// </para>
/// </remarks>
public static class EmissionFingerprint
{
    /// <summary>
    /// The <see cref="ObjectSelectionProfile"/> settings that change the BYTES of an emitted object.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same thing as "settings that change which objects are emitted". A schema
    /// filter or a type selection changes SCOPE, and scope is already handled by the planner's
    /// safety-violation rule: an object newly in scope has no prior state, which forces a full scan
    /// of its type. Folding scope into this fingerprint would invalidate every watermark for a change
    /// the planner already handles precisely, and at a million objects that is hours for nothing.
    /// </remarks>
    public static readonly IReadOnlySet<string> EmissionAffectingSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ObjectSelectionProfile.NormalizeScripts),
        nameof(ObjectSelectionProfile.IncludePermissions),
        nameof(ObjectSelectionProfile.IncludeExtendedProperties),
    };

    /// <summary>
    /// Computes the fingerprint for a job's emission configuration.
    /// </summary>
    /// <param name="selection">The job's selection profile — only the emission-affecting settings are read.</param>
    /// <param name="providerContribution">
    /// The scripting providers' own contribution: their option set and library versions. Passed in
    /// rather than read here because those live above this assembly.
    /// </param>
    public static string Compute(ObjectSelectionProfile selection, string providerContribution)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // A stable, human-readable pre-image. It is never parsed — but when a support bundle shows a
        // watermark being invalidated, being able to print what went into it is the difference
        // between diagnosing it and guessing.
        var preimage = new StringBuilder()
            .Append("normalizer=").Append(ScriptNormalizer.FormatVersion).Append(';')
            .Append("layout=").Append(ObjectFilePathMapper.LayoutVersion).Append(';')
            .Append("normalize-scripts=").Append(selection.NormalizeScripts).Append(';')
            .Append("permissions=").Append(selection.IncludePermissions).Append(';')
            .Append("extended-properties=").Append(selection.IncludeExtendedProperties).Append(';')
            .Append(providerContribution)
            .ToString();

        // Truncated to 16 hex characters: this is a change detector, not a security boundary, and a
        // watermark row is read on every run of every database.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)))[..16];
    }
}
