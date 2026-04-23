#nullable enable

using System.Text.Json;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

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
            CoreDiagnostics.WriteVerbose("LCU: Reconciliation match history returned 0 matches");
            return [];
        }

        CoreDiagnostics.WriteVerbose($"LCU: Reconciliation match history returned {matches.Count} matches");

        var dismissedIds = await _missedGameDecisionRepository.GetDismissedGameIdsAsync(
            matches.Select(static game => game.GetPropertyLongOrDefault("gameId", 0)))
            .ConfigureAwait(false);
        CoreDiagnostics.WriteVerbose($"LCU: Reconciliation dismissed ids count={dismissedIds.Count}");

        var summonerName = await TryGetCurrentSummonerNameAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<MissedGameCandidate>();

        foreach (var game in matches)
        {
            var gameId = game.GetPropertyLongOrDefault("gameId", 0);
            if (gameId <= 0 || dismissedIds.Contains(gameId))
            {
                CoreDiagnostics.WriteVerbose($"LCU: Reconciliation skipping gameId={gameId} reason={(gameId <= 0 ? "invalid" : "dismissed")}");
                continue;
            }

            var alreadySaved = checkGameSaved is not null
                ? checkGameSaved(gameId)
                : await _gameRepository.GetAsync(gameId).ConfigureAwait(false) is not null;

            if (alreadySaved)
            {
                CoreDiagnostics.WriteVerbose($"LCU: Reconciliation skipping gameId={gameId} reason=saved");
                continue;
            }

            var preferredParticipantId = TryGetPreferredParticipantId(game);
            var statsSource = game;
            if (preferredParticipantId > 0)
            {
                var detailedGame = await _lcuClient.GetMatchDetailsAsync(gameId, cancellationToken)
                    .ConfigureAwait(false);
                if (detailedGame is JsonElement detail)
                {
                    statsSource = detail;
                }
                else
                {
                    CoreDiagnostics.WriteVerbose(
                        $"LCU: Reconciliation missing detailed match payload gameId={gameId}; using summary payload");
                }
            }

            var stats = StatsExtractor.ExtractFromMatchHistory(
                statsSource,
                _logger,
                preferredParticipantId > 0 ? preferredParticipantId : null);
            if (stats is null)
            {
                CoreDiagnostics.WriteVerbose($"LCU: Reconciliation skipping gameId={gameId} reason=stats-null");
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
            CoreDiagnostics.WriteVerbose($"LCU: Reconciliation candidate gameId={gameId} champion={stats.ChampionName} win={stats.Win}");

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

    private static int TryGetPreferredParticipantId(JsonElement game)
    {
        if (game.TryGetProperty("participants", out var participants)
            && participants.ValueKind == JsonValueKind.Array)
        {
            var participantList = participants.EnumerateArray().ToList();
            if (participantList.Count == 1)
            {
                return participantList[0].GetPropertyIntOrDefault("participantId", 0);
            }
        }

        if (game.TryGetProperty("participantIdentities", out var identities)
            && identities.ValueKind == JsonValueKind.Array)
        {
            var identityList = identities.EnumerateArray().ToList();
            if (identityList.Count == 1)
            {
                return identityList[0].GetPropertyIntOrDefault("participantId", 0);
            }
        }

        return 0;
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
