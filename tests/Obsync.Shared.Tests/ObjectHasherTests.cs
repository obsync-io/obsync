using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

public sealed class ObjectHasherTests
{
    private readonly Sha256ObjectHasher _hasher = new();

    [Fact]
    public void ComputeHash_MatchesKnownSha256Vector()
    {
        // SHA-256("abc") — well-known test vector.
        var hash = _hasher.ComputeHash("abc");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void ComputeHash_IsLowercaseHex64Chars()
    {
        var hash = _hasher.ComputeHash("CREATE VIEW dbo.v AS SELECT 1;");

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void ComputeHash_IsStableForSameContent()
    {
        const string content = "CREATE PROCEDURE dbo.x AS SELECT 1;";

        Assert.Equal(_hasher.ComputeHash(content), _hasher.ComputeHash(content));
    }

    [Fact]
    public void ComputeHash_DiffersForDifferentContent()
    {
        Assert.NotEqual(_hasher.ComputeHash("a"), _hasher.ComputeHash("b"));
    }
}
