using Obsync.Shared.Models;
using Xunit;

namespace Obsync.Shared.Tests;

public sealed class JobTagsTests
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("prod", new[] { "prod" })]
    [InlineData(" prod , finance ,  pci ", new[] { "prod", "finance", "pci" })]  // trims
    [InlineData("prod,,finance", new[] { "prod", "finance" })]                    // drops blanks
    [InlineData("prod, Prod, PROD", new[] { "prod" })]                            // de-dupes case-insensitively, keeps first
    public void Parse_Normalizes(string? input, string[] expected)
    {
        Assert.Equal(expected, JobTags.Parse(input));
    }

    [Fact]
    public void IsProduction_MatchesMarker_CaseInsensitively()
    {
        string[] markers = ["prod", "production"];

        Assert.True(JobTags.IsProduction(["Finance", "PROD"], markers)); // case-insensitive hit
        Assert.True(JobTags.IsProduction(["production"], markers));
        Assert.False(JobTags.IsProduction(["staging", "finance"], markers));
        Assert.False(JobTags.IsProduction(["prod"], []));                // no markers → never production
    }

    [Fact]
    public void Classify_FlagsProductionTags_Only()
    {
        var chips = JobTags.Classify(["finance", "Prod", "pci"], ["prod", "production"]);

        Assert.Equal(3, chips.Count);
        Assert.Equal(new TagChip("finance", false), chips[0]);
        Assert.Equal(new TagChip("Prod", true), chips[1]);   // preserves original text, flagged production
        Assert.Equal(new TagChip("pci", false), chips[2]);
    }

    [Fact]
    public void Classify_Empty_IsEmpty()
    {
        Assert.Empty(JobTags.Classify([], ["prod"]));
    }
}
