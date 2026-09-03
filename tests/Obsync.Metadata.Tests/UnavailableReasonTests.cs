using Obsync.Metadata;

namespace Obsync.Metadata.Tests;

/// <summary>
/// A module whose definition the catalog will not return is either a CLR object or one created
/// WITH ENCRYPTION. Both were previously reported with a single hedged sentence — "encrypted with
/// WITH ENCRYPTION, or a CLR object" — which left the reader unable to tell a situation they can
/// do nothing about from a gap in Obsync's coverage. They are told apart by whether a
/// <c>sys.sql_modules</c> row exists at all: a CLR module has none, an encrypted one has a row
/// with a null definition.
/// </summary>
public sealed class UnavailableReasonTests
{
    [Fact]
    public void ClrAndEncrypted_ProduceDifferentReasons()
    {
        var clr = MetadataScriptProvider.UnavailableReason("module", isClr: true);
        var encrypted = MetadataScriptProvider.UnavailableReason("module", isClr: false);

        Assert.NotEqual(clr, encrypted);
    }

    [Fact]
    public void TheClrReason_NamesClrAndDoesNotBlameEncryption()
    {
        var reason = MetadataScriptProvider.UnavailableReason("module", isClr: true);

        Assert.Contains("CLR", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH ENCRYPTION", reason, StringComparison.Ordinal);
        // Points at where the object's code actually is versioned, so the reader knows what to do.
        Assert.Contains("assembly", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheEncryptedReason_NamesEncryptionAndDoesNotBlameClr()
    {
        var reason = MetadataScriptProvider.UnavailableReason("module", isClr: false);

        Assert.Contains("WITH ENCRYPTION", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("CLR", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("trigger")]
    [InlineData("DDL trigger")]
    public void TheNounIsUsed_SoEachCallSiteReadsNaturally(string noun)
    {
        Assert.Contains(noun, MetadataScriptProvider.UnavailableReason(noun, isClr: true), StringComparison.Ordinal);
        Assert.Contains(noun, MetadataScriptProvider.UnavailableReason(noun, isClr: false), StringComparison.Ordinal);
    }
}
