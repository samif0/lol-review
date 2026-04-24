#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

public sealed record EnemyLanerBackfillResult(int Scanned, int Updated, int Skipped, int Failed);

/// <summary>
/// v2.15.8: walks every game with a blank <c>enemy_laner</c> and tries to
/// resolve it via the Riot Match-V5 API (through our proxy). The DB stores
/// only a numeric <c>game_id</c>, but Match-V5 takes <c>{platform}_{gameId}</c>
/// (e.g. <c>NA1_5234567890</c>) — we reconstruct that from
/// <see cref="IConfigService.RiotRegion"/>.
///
/// Scope-A constraints:
/// - synchronous, runs to completion before returning
/// - throttles between calls so we don't slam the proxy's per-token RPS
/// - skips games already filled, hidden, or where the user's puuid isn't
///   in the participants list (a match imported via manual entry, etc.)
/// </summary>
public sealed class EnemyLanerBackfillService
{
    private readonly IGameRepository _games;
    private readonly IRiotMatchClient _matchClient;
    private readonly IConfigService _config;
    private readonly ILogger<EnemyLanerBackfillService> _logger;

    public EnemyLanerBackfillService(
        IGameRepository games,
        IRiotMatchClient matchClient,
        IConfigService config,
        ILogger<EnemyLanerBackfillService> logger)
    {
        _games = games;
        _matchClient = matchClient;
        _config = config;
        _logger = logger;
    }

    public async Task<EnemyLanerBackfillResult> RunAsync(int maxGames = int.MaxValue, CancellationToken ct = default)
    {
        var region = _config.RiotRegion;
        var puuid = _config.RiotPuuid;
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(puuid))
        {
            _logger.LogWarning("Backfill: missing RiotRegion or RiotPuuid");
            return new EnemyLanerBackfillResult(0, 0, 0, 0);
        }

        var allIds = await _games.GetGameIdsMissingEnemyLanerAsync().ConfigureAwait(false);
        // v2.15.8: auto-backfill on launch caps work so a brand-new user with
        // a thousand-game backlog doesn't trickle for an hour. Settings card
        // calls with maxGames=int.MaxValue to drain the queue manually.
        var ids = allIds.Take(maxGames).ToList();
        _logger.LogInformation("Backfill: scanning {Count} of {Total} games", ids.Count, allIds.Count);

        int scanned = 0, updated = 0, skipped = 0, failed = 0;
        var platform = region.ToUpperInvariant();

        foreach (var gameId in ids)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            // Reconstruct Match-V5 id. Riot game_ids are platform-scoped, so
            // {platform}_{gameId} maps 1:1 for games actually played on that
            // region. Manual-entry games will fail the upstream lookup —
            // we silently skip them.
            var matchId = $"{platform}_{gameId}";

            var doc = await _matchClient.GetMatchAsync(matchId, region, ct).ConfigureAwait(false);
            if (doc is not JsonElement match)
            {
                failed++;
                await Throttle(ct).ConfigureAwait(false);
                continue;
            }

            var enemy = ExtractEnemyLaner(match, puuid);
            if (string.IsNullOrEmpty(enemy))
            {
                skipped++;
                _logger.LogDebug("Backfill: no enemy resolved for game {GameId}", gameId);
                await Throttle(ct).ConfigureAwait(false);
                continue;
            }

            await _games.UpdateEnemyLanerAsync(gameId, enemy).ConfigureAwait(false);
            updated++;
            _logger.LogDebug("Backfill: game {GameId} → {Enemy}", gameId, enemy);

            await Throttle(ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Backfill done: scanned={Scanned} updated={Updated} skipped={Skipped} failed={Failed}",
            scanned, updated, skipped, failed);
        return new EnemyLanerBackfillResult(scanned, updated, skipped, failed);
    }

    /// <summary>
    /// Pull <c>info.participants[]</c>, find the row matching the user's puuid,
    /// then return the championName of the participant on the opposite team
    /// with the same teamPosition. Returns "" when any of those steps fail
    /// (e.g. ARAM has no positions, custom games have no upstream record).
    /// </summary>
    public static string ExtractEnemyLaner(JsonElement match, string puuid)
    {
        if (!match.TryGetProperty("info", out var info)) return "";
        if (!info.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array) return "";

        JsonElement self = default;
        bool foundSelf = false;
        foreach (var p in participants.EnumerateArray())
        {
            if (p.TryGetProperty("puuid", out var pPuuid)
                && pPuuid.ValueKind == JsonValueKind.String
                && string.Equals(pPuuid.GetString(), puuid, StringComparison.OrdinalIgnoreCase))
            {
                self = p;
                foundSelf = true;
                break;
            }
        }
        if (!foundSelf) return "";

        var selfTeam = self.TryGetProperty("teamId", out var stEl) && stEl.ValueKind == JsonValueKind.Number
            ? stEl.GetInt32() : 0;
        var selfPos = self.TryGetProperty("teamPosition", out var spEl) && spEl.ValueKind == JsonValueKind.String
            ? (spEl.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(selfPos)) return "";

        foreach (var p in participants.EnumerateArray())
        {
            var team = p.TryGetProperty("teamId", out var tEl) && tEl.ValueKind == JsonValueKind.Number
                ? tEl.GetInt32() : 0;
            var pos = p.TryGetProperty("teamPosition", out var pEl) && pEl.ValueKind == JsonValueKind.String
                ? (pEl.GetString() ?? "") : "";
            if (team != 0 && team != selfTeam && pos == selfPos)
            {
                return p.TryGetProperty("championName", out var cEl) && cEl.ValueKind == JsonValueKind.String
                    ? (cEl.GetString() ?? "")
                    : "";
            }
        }
        return "";
    }

    private static Task Throttle(CancellationToken ct)
    {
        // Worker per-token limit is 2 RPS. 600 ms gives headroom + smooths
        // burst pressure on Riot's underlying region rate.
        return Task.Delay(600, ct);
    }
}
