#nullable enable

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Revu.App.Composition;

internal static class AppHostFactory
{
    public static IHost CreateHost()
    {
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddDebug();
            })
            .ConfigureServices((_, services) =>
            {
                services
                    .AddDataInfrastructure()
                    .AddRepositories()
                    .AddCoreServices()
                    .AddMessaging()
                    .AddLcuServices(BypassSslValidation)
                    .AddUiServices()
                    .AddViewModels()
                    .AddStartupPipeline();
            })
            .Build();
    }

    /// <summary>
    /// The LCU (https://127.0.0.1:{port}) and Live Client API
    /// (https://127.0.0.1:2999) present Riot's self-signed certificate, which
    /// can never chain to a trusted root. Accept that ONLY for loopback hosts;
    /// any non-loopback request through these handlers must still present a
    /// valid certificate, so a future reuse of the "LcuClient"/"LiveEventApi"
    /// named clients against a remote endpoint fails closed instead of being
    /// silently MITM-able.
    /// </summary>
    private static bool BypassSslValidation(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        return request.RequestUri?.IsLoopback == true;
    }
}
