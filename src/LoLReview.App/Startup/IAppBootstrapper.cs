#nullable enable

namespace LoLReview.App.Startup;

internal interface IAppBootstrapper
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);
}
