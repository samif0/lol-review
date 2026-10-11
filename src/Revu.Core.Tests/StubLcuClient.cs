using System.Text.Json;
using Revu.Core.Lcu;
using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>3.14.1: a configurable LCU double for the remake tests (match history + details).</summary>
internal sealed class StubLcuClient : ILcuClient
{
    public bool Connected { get; set; } = true;
    public List<JsonElement> History { get; } = [];
    public Dictionary<long, JsonElement> Details { get; } = [];
    public List<long> DetailCalls { get; } = [];
    /// <summary>The client's own (UUID-shaped) puuid, returned by the current summoner.</summary>
    public string SummonerPuuid { get; set; } = "";

    public void Configure(LcuCredentials credentials) { }
    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default) =>
        Task.FromResult<JsonElement?>(RemakeTestKit.Json(SummonerPuuid.Length == 0
            ? """{ "gameName": "tester" }"""
            : $$"""{ "gameName": "tester", "puuid": "{{SummonerPuuid}}" }"""));
    public Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default) => Task.FromResult(GamePhase.None);
    public Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
    public Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default) => Task.FromResult(420);
    public Task<bool> CancelMatchmakingAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> DeclineReadyCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<List<JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default) =>
        Task.FromResult(History.ToList());

    public Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default)
    {
        DetailCalls.Add(gameId);
        return Task.FromResult<JsonElement?>(Details.TryGetValue(gameId, out var d) ? d : null);
    }

    public Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default) =>
        Task.FromResult<string?>("Ahri");
    public Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
    public Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(("", "", ""));
    public Task<ChampSelectSnapshot> GetChampSelectSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult(new ChampSelectSnapshot("", "", "", new Dictionary<string, string>()));

    /// <summary>A minimal LCU match-history game (one participant) with an optional flag.
    /// <paramref name="selfPuuid"/> adds participantIdentities naming that participant.</summary>
    public static JsonElement Game(long gameId, int duration, bool? earlySurrender, string? selfPuuid = null)
    {
        var flag = earlySurrender is bool b ? $", \"gameEndedInEarlySurrender\": {(b ? "true" : "false")}" : "";
        var identities = selfPuuid is null ? ""
            : $$""" "participantIdentities": [ { "participantId": 1, "player": { "puuid": "{{selfPuuid}}" } } ],""";
        return RemakeTestKit.Json($$"""
            {
              "gameId": {{gameId}}, "gameCreation": 1775423402532, "gameDuration": {{duration}},
              "gameMode": "CLASSIC", "gameType": "MATCHED_GAME", "queueId": 420,{{identities}}
              "participants": [
                { "participantId": 1, "championId": 103, "teamId": 100,
                  "stats": { "kills": 0, "deaths": 0, "assists": 0, "win": false{{flag}} },
                  "timeline": { "lane": "MIDDLE", "role": "SOLO" } }
              ]
            }
            """);
    }
}
