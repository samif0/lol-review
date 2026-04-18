#nullable enable

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Composition;

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
                    .AddCoachServices()
                    .AddViewModels()
                    .AddStartupPipeline();
            })
            .Build();
    }

    private static bool BypassSslValidation(
        HttpRequestMessage _,
        X509Certificate2? __,
        X509Chain? ___,
        SslPolicyErrors ____) => true;
}
