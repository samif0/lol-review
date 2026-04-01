#nullable enable

using LoLReview.Core.Constants;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Orchestrates game-end processing: saving stats, session logging,
/// event computation, and VOD matching.
/// Ported from Python main.py App._on_game_end.
/// </summary>
public sealed class GameService : IGameService
{
    private readonly IGameRepository _games;
    private readonly ISessionLogRepository _sessionLog;
    private readonly IGameEventsRepository _gameEvents;
    private readonly IDerivedEventsRepository _derivedEvents;
    private readonly IVodService _vodService;
    private readonly IConfigService _config;
    private readonly ILogger<GameService> _logger;

    public GameService(
        IGameRepository games,
        ISessionLogRepository sessionLog,
        IGameEventsRepository gameEvents,
        IDerivedEventsRepository derivedEvents,
        IVodService vodService,
        IConfigService config,
        ILogger<GameService> logger)
    {
        _games = games;
        _sessionLog = sessionLog;
        _gameEvents = gameEvents;
        _derivedEvents = derivedEvents;
        _vodService = vodService;
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

        // 1. Skip casual modes entirely (ARAM, Cherry, etc.)
        if (GameConstants.CasualModes.Contains(stats.GameMode.ToUpperInvariant()))
        {
            _logger.LogInformation("Casual game ({Mode}) -- skipping", stats.GameMode);
            return null;
        }

        // 1b. Skip normal/quickplay queues — only ranked (Solo/Flex) go through review
        if (!string.IsNullOrEmpty(stats.QueueType)
            && GameConstants.CasualQueueTypes.Contains(stats.QueueType))
        {
            _logger.LogInformation("Normal/quickplay queue ({Queue}) -- skipping", stats.QueueType);
            return null;
        }

        // 2. Skip remakes (games under 5 minutes)
        if (stats.GameDuration < GameConstants.RemakeThresholdS)
        {
            _logger.LogInformation("Remake detected ({Duration}s) -- skipping", stats.GameDuration);
            return null;
        }

        // 3. Save game via IGameRepository
        await _games.SaveAsync(stats).ConfigureAwait(false);

        // 4. Log session via ISessionLogRepository
        await _sessionLog.LogGameAsync(
            stats.GameId,
            stats.ChampionName,
            stats.Win,
            request.MentalRating,
            preGameMood: request.PreGameMood).ConfigureAwait(false);

        // 5. Save live events via IGameEventsRepository
        if (stats.LiveEvents is { Count: > 0 })
        {
            try
            {
                await _gameEvents.SaveEventsAsync(stats.GameId, stats.LiveEvents).ConfigureAwait(false);
                _logger.LogInformation(
                    "Saved {Count} live events for game {GameId}",
                    stats.LiveEvents.Count, stats.GameId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save live events for game {GameId}", stats.GameId);
            }

            // 6. Compute derived events via IDerivedEventsRepository
            try
            {
                var gameEvents = await _gameEvents.GetEventsAsync(stats.GameId).ConfigureAwait(false);
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

        // 7. Auto-match VOD via IVodService (if Ascent is enabled)
        if (_config.IsAscentEnabled)
        {
            try
            {
                var linkedNow = await _vodService.TryLinkRecordingAsync(stats).ConfigureAwait(false);
                if (!linkedNow)
                {
                    _ = ScheduleVodRetryAsync(stats.GameId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOD auto-match failed for game {GameId}", stats.GameId);
            }
        }

        // 8. Return game id
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
        var gameId = await _games.SaveManualAsync(
            championName, win, kills, deaths, assists,
            gameMode, notes, mistakes, wentWell, focusNext, tags).ConfigureAwait(false);

        await _sessionLog.LogGameAsync(
            gameId,
            championName,
            win,
            mentalRating).ConfigureAwait(false);

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

    // ── Private helpers ─────────────────────────────────────────────

    private async Task ScheduleVodRetryAsync(long gameId)
    {
        try
        {
            await Task.Delay(GameConstants.VodRetryDelayMs).ConfigureAwait(false);

            if (!_config.IsAscentEnabled)
            {
                return;
            }

            var game = await _games.GetAsync(gameId).ConfigureAwait(false);
            if (game == null)
            {
                return;
            }

            var linked = await _vodService.TryLinkRecordingAsync(game).ConfigureAwait(false);
            if (linked)
            {
                _logger.LogInformation("Delayed VOD retry succeeded for game {GameId}", gameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Delayed VOD retry failed for game {GameId}", gameId);
        }
    }
}
