using System.Net;
using NSubstitute;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>Covers proxy resolution: None → direct; Manual builds the WebProxy + an encoded git URL.</summary>
public sealed class ProxyProviderTests
{
    private static ProxyProvider Build(ProxySettings settings, string? password = null)
    {
        var repo = Substitute.For<IAppSettingsRepository>();
        repo.GetProxyAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var credentials = Substitute.For<ICredentialStore>();
        credentials.Retrieve(CredentialKeys.Proxy()).Returns(password);
        return new ProxyProvider(repo, credentials);
    }

    [Fact]
    public async Task Resolve_None_IsDirect()
    {
        var resolution = await Build(new ProxySettings { Mode = ProxyMode.None }).ResolveAsync();
        Assert.Null(resolution);
    }

    [Fact]
    public async Task Resolve_ManualWithAuth_BuildsProxyBypassAndEncodedGitUrl()
    {
        var settings = new ProxySettings
        {
            Mode = ProxyMode.Manual,
            Url = "http://proxy.corp:8080",
            Username = "svc user",
            BypassHosts = ["github.internal", "   "],
        };
        var resolution = await Build(settings, password: "p@ss:word").ResolveAsync();

        Assert.NotNull(resolution);
        var webProxy = Assert.IsType<WebProxy>(resolution!.WebProxy);
        Assert.Equal("proxy.corp", webProxy.Address!.Host);
        Assert.Equal(8080, webProxy.Address.Port);
        Assert.Contains("github.internal", webProxy.BypassList);
        Assert.DoesNotContain("   ", webProxy.BypassList); // blanks dropped
        // Credentials are URL-encoded in the git proxy URL.
        Assert.Equal("http://svc%20user:p%40ss%3Aword@proxy.corp:8080", resolution.GitProxyUrl);
    }

    [Fact]
    public async Task Resolve_ManualNoAuth_HasNoCredentialsInGitUrl()
    {
        var settings = new ProxySettings { Mode = ProxyMode.Manual, Url = "http://proxy.corp:8080" };
        var resolution = await Build(settings).ResolveAsync();

        Assert.NotNull(resolution);
        Assert.Equal("http://proxy.corp:8080", resolution!.GitProxyUrl);
        Assert.DoesNotContain("@", resolution.GitProxyUrl!);
    }

    [Fact]
    public async Task Resolve_ManualWithBlankUrl_IsDirect()
    {
        var resolution = await Build(new ProxySettings { Mode = ProxyMode.Manual, Url = "  " }).ResolveAsync();
        Assert.Null(resolution);
    }
}
