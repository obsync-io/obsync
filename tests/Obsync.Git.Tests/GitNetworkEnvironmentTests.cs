using Obsync.Git;
using Obsync.Shared;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// The <c>GIT_CONFIG_*</c> block every network command is configured with. It is asserted directly
/// because it is the single point where the preflight probe and a real clone/fetch/push are made
/// identical — if they can drift, a green "Git connection" tick stops meaning anything.
/// </summary>
public sealed class GitNetworkEnvironmentTests
{
    private static Dictionary<string, string> Pairs(IReadOnlyDictionary<string, string> environment)
    {
        var count = int.Parse(environment["GIT_CONFIG_COUNT"]);
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            pairs[environment[$"GIT_CONFIG_KEY_{i}"]] = environment[$"GIT_CONFIG_VALUE_{i}"];
        }

        return pairs;
    }

    [Fact]
    public void NoOptions_ProducesNoConfigAtAll()
    {
        // An empty block matters: GIT_CONFIG_COUNT with no keys is a malformed config to git.
        var environment = GitNetworkEnvironment.Build(new GitNetworkOptions("https://github.com/o/r.git", null, null));

        Assert.Empty(environment);
    }

    [Fact]
    public void TheAuthHeader_IsScopedToTheRemoteOrigin()
    {
        var environment = GitNetworkEnvironment.Build(
            new GitNetworkOptions("https://github.com/o/r.git", "AUTHORIZATION: basic xyz", null));

        Assert.Equal("AUTHORIZATION: basic xyz", Pairs(environment)["http.https://github.com/.extraheader"]);
    }

    [Fact]
    public void AnExplicitDefaultPort_IsKeptInTheScope()
    {
        // Uri normalises :443 away. If the scope stopped being a literal prefix of the URL git is
        // handed, the header would silently not be sent and the push would fail to authenticate.
        var environment = GitNetworkEnvironment.Build(
            new GitNetworkOptions("https://ghe.corp.local:443/o/r.git", "AUTHORIZATION: basic xyz", null));

        Assert.True(Pairs(environment).ContainsKey("http.https://ghe.corp.local:443/.extraheader"));
    }

    [Fact]
    public void ANonHttpRemote_GetsNoAuthHeader()
    {
        var environment = GitNetworkEnvironment.Build(
            new GitNetworkOptions("git@github.com:o/r.git", "AUTHORIZATION: basic xyz", null));

        Assert.Empty(environment);
    }

    [Fact]
    public void SchannelNoRevocationCheck_KeepsTheWindowsStoreAndOnlyDropsRevocation()
    {
        // The distinction the whole setting turns on: this is the remedy for a blocked CRL/OCSP
        // responder, and it must NOT also give up the Windows trust store, or a corporate root
        // deployed by Group Policy would stop working at the same time.
        var pairs = Pairs(GitNetworkEnvironment.Build(new GitNetworkOptions(
            "https://github.com/o/r.git", null, null, GitTlsBackend.SchannelNoRevocationCheck)));

        Assert.Equal("schannel", pairs["http.sslBackend"]);
        Assert.Equal("false", pairs["http.schannelCheckRevoke"]);
    }

    [Fact]
    public void OpenSsl_SendsTheCaBundleWhenOneIsConfigured()
    {
        var pairs = Pairs(GitNetworkEnvironment.Build(new GitNetworkOptions(
            "https://github.com/o/r.git", null, null, GitTlsBackend.OpenSsl, @"C:\certs\corp.pem")));

        Assert.Equal("openssl", pairs["http.sslBackend"]);
        Assert.Equal(@"C:\certs\corp.pem", pairs["http.sslCAInfo"]);
    }

    [Fact]
    public void OpenSsl_WithoutABundle_SendsNoCaInfo()
    {
        // An empty sslCAInfo would tell OpenSSL to trust nothing, which is worse than the default.
        var pairs = Pairs(GitNetworkEnvironment.Build(new GitNetworkOptions(
            "https://github.com/o/r.git", null, null, GitTlsBackend.OpenSsl)));

        Assert.False(pairs.ContainsKey("http.sslCAInfo"));
    }

    [Fact]
    public void TheDefaultBackend_SendsNoTlsConfig()
    {
        // Existing installations must not change behaviour when they upgrade into this setting.
        var pairs = Pairs(GitNetworkEnvironment.Build(new GitNetworkOptions(
            "https://github.com/o/r.git", null, "http://proxy.corp:8080")));

        Assert.Equal("http://proxy.corp:8080", pairs["http.proxy"]);
        Assert.False(pairs.ContainsKey("http.sslBackend"));
    }

    [Fact]
    public void EveryKeyIsNumberedContiguously_AndCountMatches()
    {
        // git reads KEY_0..KEY_{COUNT-1}; a gap or a wrong count silently drops configuration —
        // including the auth header, which would look like a credentials problem.
        var environment = GitNetworkEnvironment.Build(new GitNetworkOptions(
            "https://github.com/o/r.git", "AUTHORIZATION: basic xyz", "http://proxy.corp:8080",
            GitTlsBackend.OpenSsl, @"C:\certs\corp.pem"));

        var count = int.Parse(environment["GIT_CONFIG_COUNT"]);
        Assert.Equal(4, count);
        Assert.Equal((count * 2) + 1, environment.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.True(environment.ContainsKey($"GIT_CONFIG_KEY_{i}"));
            Assert.True(environment.ContainsKey($"GIT_CONFIG_VALUE_{i}"));
        }
    }
}
