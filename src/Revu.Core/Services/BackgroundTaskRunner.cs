#nullable enable

using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>Runs intentional fire-and-forget work with consistent fault logging.</summary>
public static class BackgroundTaskRunner
{
    public static void Run(
        Func<Task> operation,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        _ = RunCoreAsync(operation, logger, operationName, cancellationToken);
    }

    public static void Run(
        Task task,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        _ = RunCoreAsync(() => task, logger, operationName, cancellationToken);
    }

    private static async Task RunCoreAsync(
        Func<Task> operation,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Background operation {OperationName} canceled", operationName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background operation {OperationName} failed", operationName);
        }
    }
}
