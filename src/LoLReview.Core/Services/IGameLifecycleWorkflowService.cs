#nullable enable

namespace LoLReview.Core.Services;

public interface IGameLifecycleWorkflowService
{
    Task<ProcessGameEndResult> ProcessGameEndAsync(
        ProcessGameEndRequest request,
        bool isRecovered = false,
        CancellationToken cancellationToken = default);

    Task<ReconcileMissedGamesResult> ReconcileMissedGamesAsync(
        ReconcileMissedGamesRequest request,
        CancellationToken cancellationToken = default);
}
