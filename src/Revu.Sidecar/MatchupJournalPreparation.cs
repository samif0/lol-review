#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

public sealed record MatchupPreparationResult(
    long? Id, bool Created, GameStats? Game, MatchupPrefillResult? Prefill, string? Error = null);

/// <summary>Shared write path for selected matches, the last match and optional post-game preparation.</summary>
public static class MatchupJournalPreparation
{
    public const string UnavailableGameReason = "That match is not available in your match history.";
    // All linked-card creators use this short gate; slow Riot lookups stay outside it.
    // A manual form and the background confirmation can therefore never create two cards.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public static bool IsEligible(GameStats? game) => game is not null && !game.IsHidden
        && (GameConstants.RankedQueueTypes.Contains(game.QueueType ?? "") || game.QueueType == "Manual");

    public static async Task<LastGameResolution> ResolveGameAsync(
        long gameId, IGameHistoryQuery games, IMatchupsRepository matchups, string? fallbackPosition = null)
    {
        var game = await games.GetAsync(gameId);
        if (!IsEligible(game)) return new LastGameResolution(null, null, null);
        return new LastGameResolution(game, MatchupPrefill.FromGame(game!, fallbackPosition),
            await matchups.GetForGameAsync(gameId));
    }

    public static async Task<MatchupPreparationResult> FromGameAsync(
        long gameId, IGameRepository games, IMatchupsRepository matchups, IConfigService config,
        EnemyLanerBackfillService backfill, ILogger log, CancellationToken ct = default)
    {
        await config.LoadAsync();
        var resolved = await ResolveGameAsync(gameId, games, matchups, config.PrimaryRole);
        if (resolved.Game is null) return new(null, false, null, null, UnavailableGameReason);
        if (resolved.Existing is { } existing) return new(existing.Id, false, resolved.Game, resolved.Prefill);
        if (resolved.Prefill is null) return new(null, false, resolved.Game, null, MatchupsSnapshotBuilder.NoPrefillReason);

        var (game, prefill) = await MatchupFromLastGame.HealAsync(
            resolved.Game, resolved.Prefill, config, backfill, games, log, ct: ct);
        if (!MatchupFromLastGame.ShouldCreateOutright(game, prefill)) return new(null, false, game, prefill);

        var (id, created) = await CreateLinkedAsync(
            gameId, games, matchups, prefill.Lane, prefill.AllyChamps, prefill.EnemyChamps, ct: ct);
        return new(id, created, game, prefill);
    }

    /// <summary>Returns null while disabled or while the matchup still needs the player's confirmation.</summary>
    public static async Task<MatchupPreparationResult?> TryAutoPrepareAsync(
        long gameId, IGameHistoryQuery games, IMatchupsRepository matchups, IConfigService config,
        CancellationToken ct = default)
    {
        var settings = await config.LoadAsync();
        if (!settings.AutoMatchupNotesEnabled) return null;
        var resolved = await ResolveGameAsync(gameId, games, matchups, config.PrimaryRole);
        if (resolved.Game is null) return null;
        if (resolved.Existing is { } existing) return new(existing.Id, false, resolved.Game, resolved.Prefill);
        if (resolved.Prefill is not { } prefill || !MatchupFromLastGame.ShouldCreateOutright(resolved.Game, prefill)) return null;
        var (id, created) = await CreateLinkedAsync(
            gameId, games, matchups, prefill.Lane, prefill.AllyChamps, prefill.EnemyChamps, ct: ct);
        return new(id, created, resolved.Game, prefill);
    }

    public static async Task<(long Id, bool Created)> CreateLinkedAsync(
        long gameId, IGameHistoryQuery games, IMatchupsRepository matchups, string? lane,
        IEnumerable<string?>? allyChamps, IEnumerable<string?>? enemyChamps,
        string? prior = null, string? observed = null, bool fromManualForm = false, CancellationToken ct = default)
    {
        await CreateGate.WaitAsync(ct);
        try
        {
            if (!IsEligible(await games.GetAsync(gameId))) throw new ArgumentException(UnavailableGameReason);
            if (await matchups.GetForGameAsync(gameId) is { } existing)
            {
                if (fromManualForm)
                {
                    var requestedKey = MatchupLanes.Key(lane ?? "", MatchupLanes.NormalizeChampions(allyChamps), MatchupLanes.NormalizeChampions(enemyChamps));
                    var existingKey = MatchupLanes.Key(existing.Lane, existing.AllyChamps, existing.EnemyChamps);
                    static bool Conflicts(string saved, string? typed) => !string.IsNullOrWhiteSpace(saved)
                        && !string.IsNullOrWhiteSpace(typed) && saved != typed.Trim();
                    if (requestedKey != existingKey || Conflicts(existing.Prior, prior) || Conflicts(existing.Observed, observed))
                        throw new ArgumentException("A note already exists for this match with different details. Open its existing note to make these changes.");
                }
                // If a prepared card arrived while a partial form was open, retain
                // the player's new text without replacing notes already on the card.
                var fillPrior = string.IsNullOrWhiteSpace(existing.Prior) && !string.IsNullOrWhiteSpace(prior) ? prior : null;
                var fillObserved = string.IsNullOrWhiteSpace(existing.Observed) && !string.IsNullOrWhiteSpace(observed) ? observed : null;
                if (fillPrior is not null || fillObserved is not null)
                    await matchups.UpdateNotesAsync(existing.Id, fillPrior, fillObserved);
                return (existing.Id, false);
            }
            var id = await matchups.CreateAsync(lane, allyChamps, enemyChamps, prior, observed, gameId);
            return (id, true);
        }
        finally { CreateGate.Release(); }
    }
}
