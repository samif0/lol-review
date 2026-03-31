#nullable enable

using System.Text.Json;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

public sealed class MatchHistoryReconciliationService : IMatchHistoryReconciliationService
{
    private readonly ILcuClient _lcuClient;
    private readonly IGameRepository _gameRepository;
    private readonly IMissedGameDecisionRepository _missedGameDecisionRepository;
    private readonly ILogger<MatchHistoryReconciliationService> _logger;

    public MatchHistoryReconciliationService(
        ILcuClient lcuClient,
        IGameRepository gameRepository,
        IMissedGameDecisionRepository missedGameDecisionRepository,
        ILogger<MatchHistoryReconciliationService> logger)
    {
        _lcuClient = lcuClient;
        _gameRepository = gameRepository;
        _missedGameDecisionRepository = missedGameDecisionRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MissedGameCandidate>> FindMissedGamesAsync(
        Func<long, bool>? checkGameSaved,
        CancellationToken cancellationToken = default)
    {
        List<JsonElement> matches;
        try
        {
            matches = await _lcuClient.GetMatchHistoryAsync(begin: 0, count: 10, ct: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reconciliation: failed to fetch match history");
            return [];
        }

        if (matches.Count == 0)
        {
            return [];
        }

        var dismissedIds = await _missedGameDecisionRepository.GetDismissedGameIdsAsync(
            matches.Select(static game => game.GetPropertyLongOrDefault("gameId", 0)))
            .ConfigureAwait(false);

        var summonerName = await TryGetCurrentSummonerNameAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<MissedGameCandidate>();

        foreach (var game in matches)
        {
            var gameId = game.GetPropertyLongOrDefault("gameId", 0);
            if (gameId <= 0 || dismissedIds.Contains(gameId))
            {
                continue;
            }

            var alreadySaved = checkGameSaved is not null
                ? checkGameSaved(gameId)
                : await _gameRepository.GetAsync(gameId).ConfigureAwait(false) is not null;

            if (alreadySaved)
            {
                continue;
            }

            var stats = StatsExtractor.ExtractFromMatchHistory(game, _logger);
            if (stats is null)
            {
                continue;
            }

            if ((string.IsNullOrWhiteSpace(stats.ChampionName)
                    || stats.ChampionName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                && stats.ChampionId > 0)
            {
                var resolvedChampionName = await _lcuClient.GetChampionNameAsync(stats.ChampionId, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolvedChampionName))
                {
                    stats.ChampionName = resolvedChampionName;
                }
            }

            if (!string.IsNullOrWhiteSpace(summonerName))
            {
                stats.SummonerName = summonerName;
            }

            _logger.LogInformation(
                "Reconciliation: found unsaved recent game {GameId} ({Champion} {Result})",
                gameId,
                stats.ChampionName,
                stats.Win ? "W" : "L");

            candidates.Add(new MissedGameCandidate(gameId, stats.Timestamp, stats));
        }

        if (candidates.Count == 0)
        {
            _logger.LogDebug("Reconciliation: no missed games found");
            return [];
        }

        _logger.LogInformation("Reconciliation: found {Count} missed recent game(s)", candidates.Count);
        return candidates;
    }

    private async Task<string?> TryGetCurrentSummonerNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summoner = await _lcuClient.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
            if (summoner is not JsonElement summonerElement)
            {
                return null;
            }

            return summonerElement.GetPropertyOrDefault("displayName", "") is { Length: > 0 } displayName
                ? displayName
                : summonerElement.GetPropertyOrDefault("gameName", "Unknown");
        }
        catch
        {
            return null;
        }
    }
}
