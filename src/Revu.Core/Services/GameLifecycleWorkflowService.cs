#nullable enable

using Revu.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

public sealed class GameLifecycleWorkflowService : IGameLifecycleWorkflowService
{
    private readonly IGameService _gameService;
    private readonly IMissedGameDecisionRepository _missedGameDecisionRepository;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly ICoachSidecarNotifier _coachNotifier;
    private readonly ILogger<GameLifecycleWorkflowService> _logger;

    public GameLifecycleWorkflowService(
        IGameService gameService,
        IMissedGameDecisionRepository missedGameDecisionRepository,
        IObjectivesRepository objectivesRepository,
        ICoachSidecarNotifier coachNotifier,
        ILogger<GameLifecycleWorkflowService> logger)
    {
        _gameService = gameService;
        _missedGameDecisionRepository = missedGameDecisionRepository;
        _objectivesRepository = objectivesRepository;
        _coachNotifier = coachNotifier;
        _logger = logger;
    }

    public async Task<ProcessGameEndResult> ProcessGameEndAsync(
        ProcessGameEndRequest request,
        bool isRecovered = false,
        CancellationToken cancellationToken = default)
    {
        var gameId = await _gameService.ProcessGameEndAsync(request, cancellationToken).ConfigureAwait(false);
        if (gameId is null)
        {
            return new ProcessGameEndResult(null, IsSkipped: true, IsRecovered: isRecovered);
        }

        if (request.PreGamePracticedObjectiveIds is { Count: > 0 })
        {
            foreach (var objectiveId in request.PreGamePracticedObjectiveIds)
            {
                await _objectivesRepository.RecordGameAsync(gameId.Value, objectiveId, practiced: true).ConfigureAwait(false);
            }
        }

        // Fire-and-forget sidecar notify. Coach sidecar may be off, not
        // installed, or not healthy — the notifier implementation swallows
        // those cases and returns immediately.
        _ = _coachNotifier.NotifyGameEndedAsync(gameId.Value, cancellationToken)
            .ContinueWith(
                t => _logger.LogDebug(t.Exception, "Coach NotifyGameEndedAsync failed (non-fatal)"),
                TaskContinuationOptions.OnlyOnFaulted);

        return new ProcessGameEndResult(gameId.Value, IsSkipped: false, IsRecovered: isRecovered);
    }

    public async Task<ReconcileMissedGamesResult> ReconcileMissedGamesAsync(
        ReconcileMissedGamesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DismissedGameIds.Count > 0)
        {
            await _missedGameDecisionRepository.MarkDismissedAsync(request.DismissedGameIds).ConfigureAwait(false);
        }

        var ingestedCount = 0;
        foreach (var candidate in request.SelectedGames.OrderBy(static game => game.Timestamp))
        {
            var result = await ProcessGameEndAsync(
                new ProcessGameEndRequest(
                    candidate.Stats,
                    request.MentalRating,
                    request.PreGameMood),
                isRecovered: true,
                cancellationToken).ConfigureAwait(false);

            if (result.WasSaved)
            {
                ingestedCount++;
            }
        }

        _logger.LogInformation(
            "Missed game reconciliation completed: selected={Selected} ingested={Ingested} dismissed={Dismissed}",
            request.SelectedGames.Count,
            ingestedCount,
            request.DismissedGameIds.Count);

        return new ReconcileMissedGamesResult(
            CandidateCount: request.SelectedGames.Count + request.DismissedGameIds.Count,
            SelectedCount: request.SelectedGames.Count,
            IngestedCount: ingestedCount,
            DismissedCount: request.DismissedGameIds.Count);
    }
}
