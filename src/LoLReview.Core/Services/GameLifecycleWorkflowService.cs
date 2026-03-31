#nullable enable

using LoLReview.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

public sealed class GameLifecycleWorkflowService : IGameLifecycleWorkflowService
{
    private readonly IGameService _gameService;
    private readonly IMissedGameDecisionRepository _missedGameDecisionRepository;
    private readonly ILogger<GameLifecycleWorkflowService> _logger;

    public GameLifecycleWorkflowService(
        IGameService gameService,
        IMissedGameDecisionRepository missedGameDecisionRepository,
        ILogger<GameLifecycleWorkflowService> logger)
    {
        _gameService = gameService;
        _missedGameDecisionRepository = missedGameDecisionRepository;
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
