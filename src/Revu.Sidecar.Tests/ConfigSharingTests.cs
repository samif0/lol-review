using Microsoft.Extensions.DependencyInjection;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// One ConfigService serves both graphs: a write through WriteServices is what every
/// read-side snapshot builder sees next, with one lock and one cache for config.json.
/// </summary>
public sealed class ConfigSharingTests
{
    [Fact]
    public void WriteGraph_ResolvesTheReadGraphsConfigServiceInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSidecarServices(isolatedHostTest: true);
        using var provider = services.BuildServiceProvider();

        var read = provider.GetRequiredService<IConfigService>();
        var write = provider.GetRequiredService<WriteServices>();

        Assert.Same(read, write.Config);
    }
}
