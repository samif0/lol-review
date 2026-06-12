#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

public sealed record LaningBackfillResult(int Scanned, int Updated, int Skipped, int Failed);

/// <summary>
/// v2.18 (schema v5): walks games missing laning-at-10 numbers and resolves
/// them via Match-V5 + its timeline endpoint (through the proxy). Two
/// round-trips per game — match for lane-opponent identity, timeline for the
/// 10-minute frames — so the throttle is per-call, mirroring
/// <see cref="EnemyLanerBackfillService"/>.
///
/// Degrades silently when the proxy hasn't been redeployed with the
/// /timeline route yet (404 → skip) — re-running later fills the gap.
/// </summary>
public sealed class LaningBackfillService
{
    private readonly IGameRepository _games;
    private readonly IRiotMatchClient _matchClient;
    private readonly IConfigService _config;
    private readonly ILogger<LaningBackfillService> _logger;

    public LaningBackfillService(
        IGameRepository games,
        IRiotMatchClient matchClient,
        IConfigService config,
        ILogger<LaningBackfillService> logger)
    {
        _games = games;
        _matchClient = matchClient;
        _config = config;
        _logger = logger;
    }

    public async Task<LaningBackfillResult> RunAsync(int maxGames = int.MaxValue, CancellationToken ct = default)
    {
        var region = _config.RiotRegion;
        var puuid = _config.RiotPuuid;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(puuid))
        {
            _logger.LogDebug("Laning backfill: missing RiotRegion or RiotPuuid");
            return new LaningBackfillResult(0, 0, 0, 0);
        }

        var allIds = await _games.GetGameIdsMissingLaningAsync().ConfigureAwait(false);
        var ids = allIds.Take(maxGames).ToList();
        if (ids.Count == 0)
        {
            return new LaningBackfillResult(0, 0, 0, 0);
        }

        _logger.LogInformation("Laning backfill: scanning {Count} of {Total} games", ids.Count, allIds.Count);

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

            LaningAt10? laning = null;
            try
            {
                laning = TimelineLaningExtractor.Extract(matchDoc, timelineDoc, puuid);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Laning backfill: extraction failed for game {GameId}", gameId);
            }

            if (laning is null)
            {
                // Remakes / short games / non-positional queues — nothing to store.
                skipped++;
                continue;
            }

            await _games.UpdateLaningAt10Async(gameId, laning.CsAt10, laning.GoldDiffAt10, laning.CsDiffAt10)
                .ConfigureAwait(false);
            updated++;
            _logger.LogDebug("Laning backfill: game {GameId} → cs@10={Cs} gd@10={Gold}",
                gameId, laning.CsAt10, laning.GoldDiffAt10);
        }

        _logger.LogInformation(
            "Laning backfill done: scanned={Scanned} updated={Updated} skipped={Skipped} failed={Failed}",
            scanned, updated, skipped, failed);
        return new LaningBackfillResult(scanned, updated, skipped, failed);
    }

    private static Task Throttle(CancellationToken ct)
    {
        // Worker per-token limit is 2 RPS and this service makes two calls per
        // game — 600 ms after each call keeps us comfortably under it.
        return Task.Delay(600, ct);
    }
}
