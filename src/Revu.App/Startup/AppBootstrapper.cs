#nullable enable

using Revu.App.Helpers;
using Microsoft.Extensions.Logging;

namespace Revu.App.Startup;

internal sealed class AppBootstrapper : IAppBootstrapper
{
    private readonly IEnumerable<IStartupTask> _startupTasks;
    private readonly ILogger<AppBootstrapper> _logger;

    public AppBootstrapper(
        IEnumerable<IStartupTask> startupTasks,
        ILogger<AppBootstrapper> logger)
    {
        _startupTasks = startupTasks;
        _logger = logger;
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        // Partition tasks: UI-thread tasks (AppResources) are independent of the
        // ordered DB chain (migration → safety → init) and can run concurrently.
        var uiTasks = _startupTasks.Where(t => t is IUiThreadStartupTask).ToList();
        var bgTasks  = _startupTasks.Where(t => t is not IUiThreadStartupTask).ToList();

        // Run the sequential DB chain in the background.
        async Task RunBgChain()
        {
            foreach (var task in bgTasks)
            {
                _logger.LogInformation("Running startup task {TaskName}", task.Name);
                await Task.Run(() => task.ExecuteAsync(cancellationToken), cancellationToken);
                _logger.LogInformation("Completed startup task {TaskName}", task.Name);
            }
        }

        // Run all UI-thread tasks on the dispatcher (order preserved, independent of DB).
        async Task RunUiChain()
        {
            foreach (var task in uiTasks)
            {
                _logger.LogInformation("Running startup task {TaskName}", task.Name);
                await DispatcherHelper.RunOnUIThreadAsync(() => task.ExecuteAsync(cancellationToken));
                _logger.LogInformation("Completed startup task {TaskName}", task.Name);
            }
        }

        // The two chains are independent — run them concurrently.
        await Task.WhenAll(RunBgChain(), RunUiChain());
    }
}
