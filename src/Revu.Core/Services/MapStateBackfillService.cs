#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

public sealed record MapStateBackfillResult(int Scanned, int Updated, int Skipped, int Failed);

/// <summary>
/// v3.2 (schema v11): walks games the <see cref="MapStateAnalyzer"/> hasn't processed
/// yet and resolves their map state via Match-V5 + its timeline endpoint (through the
/// proxy) — the same two round-trips per game as <see cref="LaningBackfillService"/>,
/// with the same per-call throttle. Per game it:
///   1. deletes + re-appends the derived JUNGLE_PROXIMITY rows (idempotent re-run),
///   2. persists the death map-state stamps onto the existing DEATH rows,
///   3. marks games.map_state_v so the game never re-fetches at this analyzer version.
/// Games with nothing to derive (ARAM, remakes, no jungler) still get marked —
/// "processed, empty" is a result; a FETCH failure is not, and retries next run.
/// </summary>
public sealed class MapStateBackfillService
{
    private readonly IGameRepository _games;
    private readonly IGameEventsRepository _events;
    private readonly IRiotMatchClient _matchClient;
    private readonly IConfigService _config;
    private readonly ILogger<MapStateBackfillService> _logger;

    public MapStateBackfillService(
        IGameRepository games,
        IGameEventsRepository events,
        IRiotMatchClient matchClient,
        IConfigService config,
        ILogger<MapStateBackfillService> logger)
    {
        _games = games;
        _events = events;
        _matchClient = matchClient;
        _config = config;
        _logger = logger;
    }

    public async Task<MapStateBackfillResult> RunAsync(int maxGames = int.MaxValue, CancellationToken ct = default)
    {
        var region = _config.RiotRegion;
        var puuid = _config.RiotPuuid;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(puuid))
        {
            _logger.LogDebug("Map-state backfill: missing RiotRegion or RiotPuuid");
            return new MapStateBackfillResult(0, 0, 0, 0);
        }

        var allIds = await _games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version).ConfigureAwait(false);
        var ids = allIds.Take(maxGames).ToList();
        if (ids.Count == 0)
        {
            return new MapStateBackfillResult(0, 0, 0, 0);
        }

        _logger.LogInformation("Map-state backfill: scanning {Count} of {Total} games", ids.Count, allIds.Count);

        int scanned = 0, updated = 0, skipped = 0, failed = 0;
        var platform = region.ToUpperInvariant();

        foreach (var gameId in ids)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var matchId = $"{platform}_{gameId}";

            var match = await _matchClient.GetMatchAsync(matchId, region, ct).ConfigureAwait(false);
            await Throttle(ct).ConfigureAwait(false);
            if (match is not { } matchDoc)
            {
                failed++;
                continue;
            }

            var timeline = await _matchClient.GetTimelineAsync(matchId, region, ct).ConfigureAwait(false);
            await Throttle(ct).ConfigureAwait(false);
            if (timeline is not { } timelineDoc)
            {
                failed++;
                continue;
            }

            MapStateAnalysis analysis;
            IReadOnlyList<GameEvent> stored;
            try
            {
                stored = await _events.GetEventsAsync(gameId).ConfigureAwait(false);
                analysis = MapStateAnalyzer.Analyze(matchDoc, timelineDoc, puuid, stored);
            }
            catch (Exception ex)
            {
                // Unexpected payload shape — don't mark processed, retry next run.
                _logger.LogDebug(ex, "Map-state backfill: analysis failed for game {GameId}", gameId);
                failed++;
                continue;
            }

            try
            {
                await _events.DeleteEventsByTypeAsync(gameId, GameEvent.EventTypes.JungleProximity)
                    .ConfigureAwait(false);
                if (analysis.ProximityEvents.Count > 0)
                    await _events.AppendEventsAsync(gameId, analysis.ProximityEvents).ConfigureAwait(false);
                foreach (var death in analysis.StampedDeaths)
                    await _events.UpdateEventDetailsAsync(death.Id, death.Details).ConfigureAwait(false);
                await _games.UpdateMapStateVersionAsync(gameId, MapStateAnalyzer.Version).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Map-state backfill: persist failed for game {GameId}", gameId);
                failed++;
                continue;
            }

            if (analysis.ProximityEvents.Count == 0 && analysis.StampedDeaths.Count == 0)
            {
                skipped++; // processed, nothing derivable (ARAM / remake / no jungler)
                continue;
            }

            updated++;
            _logger.LogDebug("Map-state backfill: game {GameId} → {Prox} proximity, {Deaths} deaths stamped",
                gameId, analysis.ProximityEvents.Count, analysis.StampedDeaths.Count);
        }

        _logger.LogInformation(
            "Map-state backfill done: scanned={Scanned} updated={Updated} skipped={Skipped} failed={Failed}",
            scanned, updated, skipped, failed);
        return new MapStateBackfillResult(scanned, updated, skipped, failed);
    }

    private static Task Throttle(CancellationToken ct)
    {
        // Worker per-token limit is 2 RPS and this service makes two calls per
        // game — 600 ms after each call keeps us comfortably under it.
        return Task.Delay(600, ct);
    }
}
