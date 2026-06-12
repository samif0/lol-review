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

                // v2.18 (schema v5): structured criteria are evaluated the
                // moment the game lands so pass/fail exists even if the user
                // never opens the review.
                await EvaluateCriteriaAsync(gameId.Value, objectiveId, request.Stats).ConfigureAwait(false);
            }
        }

        // Fire-and-forget sidecar notify. Coach sidecar may be off, not
        // installed, or not healthy — the notifier implementation swallows
        // those cases and returns immediately.
        BackgroundTaskRunner.Run(
            () => _coachNotifier.NotifyGameEndedAsync(gameId.Value, cancellationToken),
            _logger,
            $"coach game-ended notify {gameId.Value}",
            cancellationToken);

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

    /// <summary>
    /// v2.18 (schema v5): evaluate an objective's structured criterion against
    /// the game's stats and stamp game_objectives.criteria_met. Silent no-op
    /// when the objective has no structured criterion or the stat is missing.
    /// </summary>
    private async Task EvaluateCriteriaAsync(long gameId, long objectiveId, Models.GameStats stats)
    {
        try
        {
            var objective = await _objectivesRepository.GetAsync(objectiveId).ConfigureAwait(false);
            if (objective is null || !objective.HasStructuredCriteria)
            {
                return;
            }

            var met = ObjectiveCriteria.Evaluate(
                objective.CriteriaMetric, objective.CriteriaOp, objective.CriteriaValue, stats);
            if (met is null)
            {
                return;
            }

            await _objectivesRepository.SetCriteriaMetAsync(gameId, objectiveId, met.Value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Criteria evaluation failed for game {GameId} objective {ObjectiveId}", gameId, objectiveId);
        }
    }
}
