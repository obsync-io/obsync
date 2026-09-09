using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// The fingerprint that lets a stored hash refuse to be trusted once the configuration that produced
/// it has changed — and, more importantly, the test that keeps its hand-curated input list honest.
///
/// A fingerprint built from a curated list has exactly one weakness: an input nobody registers. The
/// classification test below is what turns that from a silent gap into a build failure.
/// </summary>
public sealed class EmissionFingerprintTests
{
    private const string Provider = "smo-options=1;smo-library=1.2.3.4";

    private static ObjectSelectionProfile Profile() => new();

    [Fact]
    public void TheSameConfiguration_ProducesTheSameFingerprint()
    {
        Assert.Equal(
            EmissionFingerprint.Compute(Profile(), Provider),
            EmissionFingerprint.Compute(Profile(), Provider));
    }

    [Theory]
    [InlineData(nameof(ObjectSelectionProfile.NormalizeScripts))]
    [InlineData(nameof(ObjectSelectionProfile.IncludePermissions))]
    [InlineData(nameof(ObjectSelectionProfile.IncludeExtendedProperties))]
    public void TogglingAnEmissionAffectingSetting_ChangesTheFingerprint(string setting)
    {
        var baseline = EmissionFingerprint.Compute(Profile(), Provider);

        var toggled = Profile();
        switch (setting)
        {
            case nameof(ObjectSelectionProfile.NormalizeScripts):
                toggled.NormalizeScripts = !toggled.NormalizeScripts;
                break;
            case nameof(ObjectSelectionProfile.IncludePermissions):
                toggled.IncludePermissions = !toggled.IncludePermissions;
                break;
            case nameof(ObjectSelectionProfile.IncludeExtendedProperties):
                toggled.IncludeExtendedProperties = !toggled.IncludeExtendedProperties;
                break;
            default:
                throw new InvalidOperationException($"Unhandled setting {setting}.");
        }

        Assert.NotEqual(baseline, EmissionFingerprint.Compute(toggled, Provider));
    }

    [Fact]
    public void AProviderChange_ChangesTheFingerprint()
    {
        // The SMO library version rides in here, so a package bump that alters emitted bytes with no
        // change to this codebase still invalidates the watermarks it would otherwise silently break.
        Assert.NotEqual(
            EmissionFingerprint.Compute(Profile(), Provider),
            EmissionFingerprint.Compute(Profile(), "smo-options=2;smo-library=1.2.3.4"));
    }

    [Fact]
    public void ChangingScopeAlone_DoesNotChangeTheFingerprint()
    {
        // Scope is deliberately excluded. An object newly in scope has no prior state, which the
        // planner's own safety-violation rule already turns into a full scan of that type — precisely
        // and cheaply. Folding scope in here would invalidate every watermark for a change that is
        // already handled, which at a million objects is hours of work for nothing.
        var narrowed = Profile();
        narrowed.SchemaFilter = ["dbo"];
        narrowed.Preset = ObjectSelectionPreset.Custom;
        narrowed.CustomTypes = [SqlObjectType.View];

        Assert.Equal(
            EmissionFingerprint.Compute(Profile(), Provider),
            EmissionFingerprint.Compute(narrowed, Provider));
    }

    /// <summary>
    /// Every setting on the selection profile must be consciously classified: either it changes the
    /// bytes Obsync emits (and belongs in the fingerprint), or it does not (and is listed here with
    /// the reason). Adding a property without doing so fails this test.
    ///
    /// This is the whole safeguard. A curated list that nobody is forced to maintain decays into the
    /// same silent staleness the fingerprint exists to prevent.
    /// </summary>
    [Fact]
    public void EverySelectionSetting_IsClassified()
    {
        // Settings that decide WHICH objects or artifacts are produced, not the bytes of an object's
        // own file. Artifacts are regenerated on every run and so cannot go stale through a skip.
        var notEmissionAffecting = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ObjectSelectionProfile.Preset),                          // scope
            nameof(ObjectSelectionProfile.CustomTypes),                     // scope
            nameof(ObjectSelectionProfile.ServerTypes),                     // scope
            nameof(ObjectSelectionProfile.SchemaFilter),                    // scope
            nameof(ObjectSelectionProfile.IgnorePatterns),                  // scope
            nameof(ObjectSelectionProfile.ReferenceDataTables),             // separate data/ scripts
            nameof(ObjectSelectionProfile.IncludeObjectInventory),          // artifact, regenerated every run
            nameof(ObjectSelectionProfile.IncludeDatabaseOptions),          // artifact, regenerated every run
            nameof(ObjectSelectionProfile.IncludeDatabasePermissionsFile),  // artifact, regenerated every run
            nameof(ObjectSelectionProfile.IncludeDocumentation),            // artifact, regenerated every run
            nameof(ObjectSelectionProfile.IncludeSecurityReview),           // artifact, regenerated every run
            nameof(ObjectSelectionProfile.RemoveDroppedObjects),            // deletion behaviour, not content
        };

        var unclassified = typeof(ObjectSelectionProfile)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => !EmissionFingerprint.EmissionAffectingSettings.Contains(name))
            .Where(name => !notEmissionAffecting.Contains(name))
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            $"ObjectSelectionProfile.{string.Join(", ", unclassified)} is not classified. If it changes "
            + "the BYTES Obsync emits for an object, add it to EmissionFingerprint.EmissionAffectingSettings "
            + "and to the Compute pre-image; if it only changes which objects are produced, list it above "
            + "with the reason. Leaving it unclassified means a change to it would silently leave every "
            + "skipped object's file stale forever.");
    }
}
