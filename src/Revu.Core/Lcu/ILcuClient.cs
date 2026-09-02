#nullable enable

using System.Text.Json;
using Revu.Core.Models;

namespace Revu.Core.Lcu;

/// <summary>
/// HTTP client for the League Client Update (LCU) API.
/// </summary>
public interface ILcuClient
{
    /// <summary>
    /// Configure the client with LCU credentials (base address and auth header).
    /// </summary>
    void Configure(LcuCredentials credentials);

    /// <summary>
    /// Check if the League client is reachable.
    /// </summary>
    Task<bool> IsConnectedAsync(CancellationToken ct = default);

    /// <summary>
    /// Get info about the logged-in summoner.
    /// </summary>
    Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the current gameflow phase.
    /// </summary>
    Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the end-of-game stats block. Only available right after a game.
    /// </summary>
    Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// v3.7 (hard stop): leave the matchmaking queue — the same request the
    /// client's own Cancel button makes (DELETE /lol-lobby/v2/lobby/matchmaking/search).
    /// Acts only on the user's own client; touches no game data. Returns false
    /// when the LCU rejects it (already out of queue, etc.).
    /// </summary>
    Task<bool> CancelMatchmakingAsync(CancellationToken ct = default);

    /// <summary>
    /// v3.7 (hard stop): decline a pending ready check
    /// (POST /lol-matchmaking/v1/ready-check/decline). Used only when a match pops
    /// inside the monitor's poll window before the search could be cancelled.
    /// Returns false when the LCU rejects it.
    /// </summary>
    Task<bool> DeclineReadyCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the queue ID from the current lobby/session. Returns -1 if unavailable.
    /// </summary>
    Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Get recent match history for the current player via LCU.
    /// </summary>
    Task<List<JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default);

    /// <summary>
    /// Get full match details for a specific game.
    /// </summary>
    Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default);

    /// <summary>
    /// Resolve a champion display name from a champion ID using the local game-data assets.
    /// Returns null if the lookup fails or the ID is unknown.
    /// </summary>
    Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default);

    /// <summary>
    /// Get current ranked stats for the player.
    /// </summary>
    Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get champion select info for the current session.
    /// Returns (myChampion, enemyLanerChampion, myPosition). Any field may be empty if
    /// unavailable. myPosition is Riot-internal: TOP|JUNGLE|MIDDLE|BOTTOM|UTILITY|"".
    /// </summary>
    Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// v2.16.4: full team snapshot of the active champ select. Returns the
    /// user's champ + position plus a role→champion map for both teams keyed
    /// from the user's perspective. Drives 2v2 matchup pairings + per-enemy
    /// cooldown cards on PreGamePage.
    /// Map keys: "ownTop"/"ownJg"/"ownMid"/"ownBot"/"ownSupp",
    /// "enemyTop"/"enemyJg"/"enemyMid"/"enemyBot"/"enemySupp". Missing keys
    /// = champion not yet locked or LCU didn't expose the slot.
    /// </summary>
    Task<ChampSelectSnapshot> GetChampSelectSnapshotAsync(CancellationToken ct = default);
}

/// <summary>v2.16.4: structured champ-select state for role-aware UIs.</summary>
public sealed record ChampSelectSnapshot(
    string MyChampion,
    string MyPosition,
    string EnemyLaner,
    System.Collections.Generic.IReadOnlyDictionary<string, string> ParticipantMap);
