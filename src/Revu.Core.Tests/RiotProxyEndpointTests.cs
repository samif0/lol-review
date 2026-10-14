using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// 4.2: REVU_PROXY_BASE_URL may point the desktop at a local wrangler dev, and nothing else.
/// </summary>
public sealed class RiotProxyEndpointTests
{
    private const string Production = "https://revu-proxy.lol-review.workers.dev";

    [Theory]
    [InlineData("http://localhost:8787", "http://localhost:8787")]
    [InlineData("http://localhost:8787/", "http://localhost:8787")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787")]
    [InlineData("  http://127.0.0.1:8787/  ", "http://127.0.0.1:8787")]
    [InlineData("http://[::1]:8787", "http://[::1]:8787")]
    [InlineData("http://LOCALHOST:9000", "http://localhost:9000")]
    [InlineData("http://localhost", "http://localhost")]
    public void LoopbackHttp_IsHonored(string value, string expected)
    {
        Assert.Equal(expected, RiotProxyEndpoint.Resolve(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://localhost:8787")]
    [InlineData("https://revu-proxy.example.dev")]
    [InlineData("http://example.com:8787")]
    [InlineData("http://192.168.1.10:8787")]
    [InlineData("http://127.0.0.2:8787")]
    [InlineData("http://localhost.evil.test:8787")]
    [InlineData("http://localhost:8787/proxy")]
    [InlineData("http://localhost:8787/proxy/")]
    [InlineData("http://user@localhost:8787")]
    [InlineData("http://user:pass@127.0.0.1:8787")]
    [InlineData("http://localhost:8787?x=1")]
    [InlineData("http://localhost:8787/?")]
    [InlineData("http://localhost:8787#frag")]
    [InlineData("ftp://localhost:8787")]
    [InlineData("localhost:8787")]
    [InlineData("//localhost:8787")]
    [InlineData("file:///C:/proxy")]
    public void AnythingElse_IsTheProductionWorker(string? value)
    {
        Assert.Equal(Production, RiotProxyEndpoint.Resolve(value));
    }

    [Fact]
    public void BaseUrl_IsProduction_WhenNoOverrideIsSet()
    {
        Assert.Equal(Production, RiotProxyEndpoint.ProductionBaseUrl);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REVU_PROXY_BASE_URL")))
            Assert.Equal(Production, RiotProxyEndpoint.BaseUrl);
    }
}
