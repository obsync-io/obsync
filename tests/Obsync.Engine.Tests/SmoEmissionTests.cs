using System.Security.Cryptography;
using System.Text;
using Obsync.Shared.Models;
using Obsync.Smo;
using Xunit;

namespace Obsync.Engine.Tests;

/// <summary>
/// SMO decides the bytes of every table Obsync writes, from an option set this codebase owns and a
/// library it does not. <see cref="SmoEmission.OptionsVersion"/> is a constant a developer has to
/// remember to bump — which is the same discipline whose absence let stale-format files accumulate
/// unnoticed in the first place. This is what makes forgetting it a build failure instead.
/// </summary>
public sealed class SmoEmissionTests
{
    /// <summary>
    /// Digest of every scripting option the factory sets, for the default selection profile. If this
    /// test fails, the option set changed: decide whether the change alters emitted bytes, bump
    /// <see cref="SmoEmission.OptionsVersion"/> if it does, then update this constant.
    /// </summary>
    private const string ExpectedOptionsDigest = "8d68e430a4b9e798";

    private static string OptionsDigest(ObjectSelectionProfile selection)
    {
        var options = SmoScriptingOptionsFactory.Create(selection);
        var canonical = new StringBuilder();
        foreach (var property in options.GetType().GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            string value;
            try
            {
                value = property.GetValue(options)?.ToString() ?? "null";
            }
            catch (Exception ex)
            {
                // A property that throws on read cannot affect emitted bytes for us either way.
                value = $"<{ex.GetType().Name}>";
            }

            canonical.Append(property.Name).Append('=').Append(value).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16];
    }

    [Fact]
    public void TheScriptingOptionSet_HasNotChangedWithoutTheVersionBeingConsidered()
    {
        var actual = OptionsDigest(new ObjectSelectionProfile());

        Assert.True(
            actual == ExpectedOptionsDigest,
            $"The SMO scripting option set produced digest '{actual}', expected '{ExpectedOptionsDigest}'. "
            + "The options, their defaults, or the SMO library version changed. If the change alters the "
            + "bytes Obsync emits, bump SmoEmission.OptionsVersion so existing watermarks are invalidated "
            + "and stale files are rewritten; then set ExpectedOptionsDigest to the value above. "
            + "Leaving both alone means every object the planner skips keeps its old-format file forever.");
    }

    [Fact]
    public void TheContribution_CarriesBothTheOptionSetAndTheLibrary()
    {
        // The library version is the half no code change of ours would ever announce.
        Assert.Contains($"smo-options={SmoEmission.OptionsVersion}", SmoEmission.Contribution, StringComparison.Ordinal);
        Assert.Contains("smo-library=", SmoEmission.Contribution, StringComparison.Ordinal);
    }

    [Fact]
    public void EmissionAffectingSelectionSettings_ReachTheOptionSet()
    {
        // These two are read by the factory, so they must also be in the fingerprint — the pairing is
        // what makes toggling one invalidate the watermarks it would otherwise silently break.
        var permissive = new ObjectSelectionProfile { IncludePermissions = true, IncludeExtendedProperties = true };
        var restrictive = new ObjectSelectionProfile { IncludePermissions = false, IncludeExtendedProperties = false };

        Assert.NotEqual(OptionsDigest(permissive), OptionsDigest(restrictive));
    }
}
