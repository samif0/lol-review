#nullable enable

namespace Revu.App.Startup;

internal interface IAppBootstrapper
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);
}
