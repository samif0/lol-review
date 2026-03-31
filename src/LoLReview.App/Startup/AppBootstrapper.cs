#nullable enable

using LoLReview.App.Helpers;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.Startup;

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
        foreach (var startupTask in _startupTasks)
        {
            _logger.LogInformation("Running startup task {TaskName}", startupTask.Name);
            if (startupTask is IUiThreadStartupTask)
            {
                await DispatcherHelper.RunOnUIThreadAsync(
                    () => startupTask.ExecuteAsync(cancellationToken));
            }
            else
            {
                await Task.Run(
                    () => startupTask.ExecuteAsync(cancellationToken),
                    cancellationToken);
            }

            _logger.LogInformation("Completed startup task {TaskName}", startupTask.Name);
        }
    }
}
