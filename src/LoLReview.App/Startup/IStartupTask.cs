#nullable enable

namespace LoLReview.App.Startup;

internal interface IStartupTask
{
    string Name { get; }

    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
