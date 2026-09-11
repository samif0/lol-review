#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// v3.9.2: the write half of "New card from last game" (POST
/// /api/matchup/from-last-game), kept out of Program.cs so it can be tested.
///
/// <para>A game recovered from the client's match history has no confirmed
/// opponents: that payload carries no per-player position, so its participant
/// map (if any) comes from Riot's lane/role heuristic and its
/// <c>enemy_laner</c> is blank. When the player is signed in,
/// <see cref="HealAsync"/> first resolves the game from Match-V5 — the same
/// single-game lookup the Settings backfill does in bulk — and re-reads the
/// row, so the card is built from <c>teamPosition</c> data. The lookup is
/// bounded by <see cref="DefaultLookupBudget"/>: the Tauri side gives the
/// whole request 30 s, and the match client sleeps through 429s (20 s by
/// default, up to 3 min) for the bulk sweep's sake, so an unbounded lookup
/// would time the request out and the page would show an error instead of a
/// card. Past the budget, or on any failure, the route degrades to what the
/// row already holds.</para>
///
/// <para>Then <see cref="MatchupPrefillResult.CanCreateOutright"/> decides:
/// both sides known and the lane from the game itself → create the card;
/// otherwise answer <c>partial</c> and the page opens the form pre-filled
/// (lane, the player's side, the game link) for the player to finish.</para>
/// </summary>
public static class MatchupFromLastGame
{
    public static readonly TimeSpan DefaultLookupBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Signed in AND the account is resolved to a PUUID — what
    /// <see cref="EnemyLanerBackfillService.BackfillGameAsync"/> actually needs,
    /// so the read snapshot's hint ("Revu will look them up") and the write
    /// route's lookup gate can never disagree.
    /// </summary>
    public static bool CanLookUpMatches(IConfigService config) =>
        config.RiotProxyEnabled && !string.IsNullOrWhiteSpace(config.RiotPuuid);

    /// <summary>
    /// The row's opponents were never confirmed: the enemy side is unknown, or
    /// <c>enemy_laner</c> is blank — it is written only by the live EOG capture
    /// and the Match-V5 backfill, so blank means "recovered from the client's
    /// match history" and any map on the row is heuristic.
    /// </summary>
    public static bool NeedsLookup(GameStats game, MatchupPrefillResult prefill) =>
        !prefill.IsComplete
        || string.IsNullOrEmpty(game.EnemyLaner)
        // v3.10.1: an estimate stamped at game end (champ select / role priors)
        // fills the card immediately; the bounded lookup confirms it when it can.
        || MatchupSources.NeedsConfirmation(game.MatchupSource);

    public static async Task<(GameStats Game, MatchupPrefillResult Prefill)> HealAsync(
        GameStats game,
        MatchupPrefillResult prefill,
        IConfigService config,
        EnemyLanerBackfillService backfill,
        IGameRepository games,
        ILogger log,
        TimeSpan? lookupBudget = null,
        CancellationToken ct = default)
    {
        if (!NeedsLookup(game, prefill) || !CanLookUpMatches(config)) return (game, prefill);

        var budget = lookupBudget ?? DefaultLookupBudget;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(budget);
        try
        {
            var outcome = await backfill.BackfillGameAsync(game.GameId, bounded.Token);
            log.LogInformation("Matchup pre-fill: Match-V5 lookup for game {GameId} → {Outcome}", game.GameId, outcome);
            if (outcome != EnemyLanerBackfillOutcome.Updated) return (game, prefill);

            var refreshed = await games.GetAsync(game.GameId);
            if (refreshed is null) return (game, prefill);
            var healed = MatchupPrefill.FromGame(refreshed, config.PrimaryRole);
            return healed is null ? (game, prefill) : (refreshed, healed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            log.LogInformation(
                "Matchup pre-fill: Match-V5 lookup for game {GameId} exceeded {Seconds:0}s — using what the row holds",
                game.GameId, budget.TotalSeconds);
            return (game, prefill);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Matchup pre-fill: Match-V5 lookup failed for game {GameId} — using what the row holds", game.GameId);
            return (game, prefill);
        }
    }
}
