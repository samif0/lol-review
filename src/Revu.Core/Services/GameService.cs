#nullable enable

using Revu.Core.Constants;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Orchestrates game-end processing: saving stats, session logging,
/// and event computation.
/// Ported from Python main.py App._on_game_end.
/// </summary>
public sealed class GameService : IGameService
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IGameRepository _games;
    private readonly ISessionLogRepository _sessionLog;
    private readonly IRulesRepository _rules;
    private readonly IGameEventsRepository _gameEvents;
    private readonly IDerivedEventsRepository _derivedEvents;
    private readonly IConfigService _config;
    private readonly ILogger<GameService> _logger;

    public GameService(
        IDbConnectionFactory dbFactory,
        IGameRepository games,
        ISessionLogRepository sessionLog,
        IRulesRepository rules,
        IGameEventsRepository gameEvents,
        IDerivedEventsRepository derivedEvents,
        IConfigService config,
        ILogger<GameService> logger)
    {
        _dbFactory = dbFactory;
        _games = games;
        _sessionLog = sessionLog;
        _rules = rules;
        _gameEvents = gameEvents;
        _derivedEvents = derivedEvents;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long?> ProcessGameEndAsync(
        ProcessGameEndRequest request,
        CancellationToken cancellationToken = default)
    {
        var stats = request.Stats;
        if (stats.GameId <= 0)
        {
            _logger.LogWarning(
                "Skipping game-end processing for invalid game id {GameId} ({Champion})",
                stats.GameId,
                stats.ChampionName);
            return null;
        }

        _logger.LogInformation(
            "Game ended: {Champion} {Result} {Kills}/{Deaths}/{Assists} ({Mode})",
            stats.ChampionName,
            stats.Win ? "Win" : "Loss",
            stats.Kills, stats.Deaths, stats.Assists,
            stats.GameMode);

        // 1. Only Ranked Solo/Duo games go through review
        if (!GameConstants.RankedQueueTypes.Contains(stats.QueueType ?? ""))
        {
            _logger.LogInformation("Non-ranked game ({Queue}, {Mode}) -- skipping", stats.QueueType, stats.GameMode);
            return null;
        }

        // 2. Remakes (League early surrender) are saved and reviewable but never counted.
        //    An unflagged game under RemakeThresholdS is still skipped as before (invalid or short).
        var isRemake = stats.EndedInEarlySurrender == true;
        if (!isRemake && stats.GameDuration < GameConstants.RemakeThresholdS)
        {
            _logger.LogInformation("Short unflagged game ({Duration}s) -- skipping", stats.GameDuration);
            return null;
        }
        stats.IsRemake = isRemake;

        // 3. Reconcile the captured account id to the SIGNED-IN Riot PUUID before
        // saving. The LCU end-of-game localPlayer.puuid is the local (logged-in)
        // player, so a captured game is always the current user's — but the LCU
        // reports a UUID-shaped id (e.g. 07f45763-…) that does NOT equal the
        // encrypted Riot PUUID our login stores in config. Account scoping
        // string-compares the two (SessionLogRepository.CurrentAccountFilter), so
        // leaving the LCU id strands every captured game behind the filter (the
        // "GAMES 0 even though I played" dashboard bug). Stamp the config PUUID
        // when we have one; logged out ('') keeps whatever the LCU gave (the
        // lenient '' scope still treats it as the user's own). This is the single
        // save chokepoint for every path — live capture, recovered, and
        // batch-ingested missed reviews — so reconcile here, once.
        var riotPuuid = _config.RiotPuuid;
        if (!string.IsNullOrWhiteSpace(riotPuuid)
            && !string.Equals(stats.Puuid, riotPuuid, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Reconciling captured PUUID '{Lcu}' -> signed-in Riot PUUID for game {GameId}",
                string.IsNullOrEmpty(stats.Puuid) ? "(empty)" : stats.Puuid,
                stats.GameId);
            stats.Puuid = riotPuuid;
        }

        // 4. Check user-defined rule violations. A remake never breaks a rule (it is not
        // counted). These are reads, so they run before the write unit below; today's
        // session rows do not include this game yet either way.
        var ruleBroken = false;
        if (!isRemake)
        {
            try
            {
                var todaysGames = await _sessionLog.GetTodayAsync().ConfigureAwait(false);
                var gameChecks = todaysGames
                    .Select(e => new RuleCheckGame(e.GameId ?? 0, e.Win, e.ChampionName, e.Timestamp))
                    .ToList();
                // Pass null for mentalRating: min_mental is a pre-queue gate and cannot
                // be evaluated retroactively from the post-game review rating. Clock rules
                // judge when the game was queued (its start), like the Rules evidence pass,
                // not when it ended or was ingested.
                DateTimeOffset? queuedAt = stats.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(stats.Timestamp)
                    : null;
                var violations = await _rules.CheckViolationsAsync(gameChecks, mentalRating: null, asOf: queuedAt)
                    .ConfigureAwait(false);
                ruleBroken = violations.Any(v => v.Violated);

                // Sticky clear: a prior user clear for this game wins over re-eval.
                if (ruleBroken && await _sessionLog.IsRuleBreakClearedAsync(stats.GameId).ConfigureAwait(false))
                {
                    ruleBroken = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rule violation check failed for game {GameId} — defaulting to no violation", stats.GameId);
            }
        }

        // 5. Save the game, its remake verdict and its session row as one transaction. A
        // failure leaves none of them, so the next capture or the missed-game reconcile
        // saves the game whole instead of finding a games row with no session row. Its
        // mood and intention are logged for a remake too.
        using (var session = DbWriteSession.Begin(_dbFactory))
        {
            await _games.SaveAsync(stats, session).ConfigureAwait(false);

            // The capture's remake verdict (true or false). Never fails the save: a
            // failure rolls back only the verdict.
            if (stats.EndedInEarlySurrender is bool flag)
            {
                var failure = await session.TryStepAsync(() => _games.SetRemakeVerdictAsync(stats.GameId, flag,
                    stats.CausedEarlySurrender, stats.EarlySurrenderSource ?? RemakeSql.SourceEog, session))
                    .ConfigureAwait(false);
                if (failure is not null)
                    _logger.LogWarning(failure, "Could not record the remake verdict for game {GameId}", stats.GameId);
                else if (flag)
                    _logger.LogInformation("Game {GameId} ended in a remake: saved, not counted", stats.GameId);
            }

            await _sessionLog.LogGameAsync(
                stats.GameId,
                stats.ChampionName,
                stats.Win,
                request.MentalRating,
                preGameMood: request.PreGameMood,
                ruleBroken: ruleBroken,
                pregameIntention: request.PregameIntention,
                intentionSource: request.IntentionSource,
                session: session).ConfigureAwait(false);
            session.Commit();
        }

        // 6. Save live events via IGameEventsRepository
        if (stats.EventProcessingReport is { } processingReport)
        {
            try { await _gameEvents.SaveProcessingReportAsync(stats.GameId, processingReport).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not persist processing coverage for {GameId}", stats.GameId); }
        }
        if (stats.LiveEvents is { Count: > 0 })
        {
            try
            {
                await _gameEvents.SaveEventsAsync(stats.GameId, stats.LiveEvents).ConfigureAwait(false);
                _logger.LogInformation(
                    "Saved {Count} live events for game {GameId}",
                    stats.LiveEvents.Count, stats.GameId);
                // The save replaced every derived row (JUNGLE_PROXIMITY, TEAMFIGHT) and
                // every death stamp for this game. A re-captured game must not stay
                // marked "processed" with nothing derived — re-queue the map-state pass.
                await _games.ClearMapStateVersionAsync(stats.GameId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save live events for game {GameId}", stats.GameId);
            }

            // 7. Compute derived events via IDerivedEventsRepository
            try
            {
                var gameEvents = await _gameEvents.GetEligibleEventsAsync(stats.GameId).ConfigureAwait(false);
                var definitions = await _derivedEvents.GetAllDefinitionsAsync().ConfigureAwait(false);
                var instances = _derivedEvents.ComputeInstances(stats.GameId, gameEvents, definitions);
                if (instances.Count > 0)
                {
                    await _derivedEvents.SaveInstancesAsync(stats.GameId, instances).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Computed {Count} derived event instances for game {GameId}",
                        instances.Count, stats.GameId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute derived events for game {GameId}", stats.GameId);
            }
        }

        // Native recordings attach separately by exact session and match identity.
        return stats.GameId;
    }

    /// <inheritdoc />
    public async Task<long> ProcessManualEntryAsync(
        string championName,
        bool win,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        string gameMode = "Manual Entry",
        string notes = "",
        string mistakes = "",
        string wentWell = "",
        string focusNext = "",
        List<string>? tags = null,
        int mentalRating = 5)
    {
        long gameId;
        using (var session = DbWriteSession.Begin(_dbFactory))
        {
            gameId = await _games.SaveManualAsync(
                championName, win, kills, deaths, assists,
                gameMode, notes, mistakes, wentWell, focusNext, tags, session).ConfigureAwait(false);

            await _sessionLog.LogGameAsync(
                gameId,
                championName,
                win,
                mentalRating,
                session: session).ConfigureAwait(false);
            session.Commit();
        }

        _logger.LogInformation("Manual entry saved: {Champion} {Result}, game_id={GameId}",
            championName, win ? "Win" : "Loss", gameId);

        return gameId;
    }

    /// <inheritdoc />
    public async Task SaveReviewAsync(long gameId, GameReview review)
    {
        await _games.UpdateReviewAsync(gameId, review).ConfigureAwait(false);
        _logger.LogInformation("Review saved for game {GameId}", gameId);
    }

    /// <inheritdoc />
    public async Task<GameStats?> GetGameForReviewAsync(long gameId)
    {
        return await _games.GetAsync(gameId).ConfigureAwait(false);
    }

}
