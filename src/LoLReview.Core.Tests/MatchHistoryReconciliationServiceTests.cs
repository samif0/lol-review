using System.Text.Json;
using LoLReview.Core.Lcu;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoLReview.Core.Tests;

public sealed class MatchHistoryReconciliationServiceTests
{
    [Fact]
    public async Task FindMissedGamesAsync_UsesFullMatchDetailsToComputeKillParticipation()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var summaryMatch = ParseJson(
            """
            {
              "gameId": 5531339140,
              "gameCreation": 1775423402532,
              "gameDuration": 2406,
              "gameMode": "CLASSIC",
              "gameType": "MATCHED_GAME",
              "queueId": 420,
              "participantIdentities": [
                {
                  "participantId": 9,
                  "player": {
                    "gameName": "chapy",
                    "puuid": "current-puuid"
                  }
                }
              ],
              "participants": [
                {
                  "participantId": 9,
                  "championId": 145,
                  "teamId": 200,
                  "spell1Id": 21,
                  "spell2Id": 4,
                  "stats": {
                    "kills": 3,
                    "deaths": 4,
                    "assists": 6,
                    "totalMinionsKilled": 346,
                    "neutralMinionsKilled": 7,
                    "champLevel": 18,
                    "visionScore": 25,
                    "goldEarned": 18407,
                    "goldSpent": 18400,
                    "largestKillingSpree": 3,
                    "largestMultiKill": 1,
                    "doubleKills": 0,
                    "tripleKills": 0,
                    "quadraKills": 0,
                    "pentaKills": 0,
                    "totalDamageDealt": 280319,
                    "totalDamageDealtToChampions": 27389,
                    "physicalDamageDealtToChampions": 9287,
                    "magicDamageDealtToChampions": 17563,
                    "trueDamageDealtToChampions": 538,
                    "totalDamageTaken": 27478,
                    "damageSelfMitigated": 18924,
                    "largestCriticalStrike": 0,
                    "wardsPlaced": 12,
                    "wardsKilled": 3,
                    "visionWardsBoughtInGame": 0,
                    "turretKills": 1,
                    "inhibitorKills": 0,
                    "totalHeal": 4685,
                    "totalHealsOnTeammates": 0,
                    "totalDamageShieldedOnTeammates": 0,
                    "totalTimeCrowdControlDealt": 159,
                    "timeCCingOthers": 6,
                    "spell1Casts": 0,
                    "spell2Casts": 0,
                    "spell3Casts": 0,
                    "spell4Casts": 0,
                    "win": false
                  },
                  "timeline": {
                    "lane": "BOTTOM",
                    "role": "CARRY"
                  }
                }
              ]
            }
            """);

        var detailMatch = ParseJson(
            """
            {
              "gameId": 5531339140,
              "gameCreation": 1775423402532,
              "gameDuration": 2406,
              "gameMode": "CLASSIC",
              "gameType": "MATCHED_GAME",
              "queueId": 420,
              "participants": [
                {
                  "participantId": 1,
                  "championId": 266,
                  "teamId": 100,
                  "stats": { "kills": 10, "deaths": 5, "assists": 12, "win": true },
                  "timeline": { "lane": "TOP", "role": "SOLO" }
                },
                {
                  "participantId": 2,
                  "championId": 120,
                  "teamId": 100,
                  "stats": { "kills": 7, "deaths": 8, "assists": 5, "win": true },
                  "timeline": { "lane": "JUNGLE", "role": "NONE" }
                },
                {
                  "participantId": 3,
                  "championId": 101,
                  "teamId": 100,
                  "stats": { "kills": 5, "deaths": 5, "assists": 8, "win": true },
                  "timeline": { "lane": "MIDDLE", "role": "SOLO" }
                },
                {
                  "participantId": 4,
                  "championId": 222,
                  "teamId": 100,
                  "stats": { "kills": 10, "deaths": 3, "assists": 12, "win": true },
                  "timeline": { "lane": "BOTTOM", "role": "CARRY" }
                },
                {
                  "participantId": 5,
                  "championId": 412,
                  "teamId": 100,
                  "stats": { "kills": 0, "deaths": 3, "assists": 23, "win": true },
                  "timeline": { "lane": "BOTTOM", "role": "SUPPORT" }
                },
                {
                  "participantId": 6,
                  "championId": 86,
                  "teamId": 200,
                  "stats": { "kills": 9, "deaths": 10, "assists": 1, "win": false },
                  "timeline": { "lane": "TOP", "role": "SOLO" }
                },
                {
                  "participantId": 7,
                  "championId": 107,
                  "teamId": 200,
                  "stats": { "kills": 8, "deaths": 6, "assists": 6, "win": false },
                  "timeline": { "lane": "JUNGLE", "role": "NONE" }
                },
                {
                  "participantId": 8,
                  "championId": 268,
                  "teamId": 200,
                  "stats": { "kills": 5, "deaths": 7, "assists": 7, "win": false },
                  "timeline": { "lane": "MIDDLE", "role": "SOLO" }
                },
                {
                  "participantId": 9,
                  "championId": 145,
                  "teamId": 200,
                  "spell1Id": 21,
                  "spell2Id": 4,
                  "stats": {
                    "kills": 3,
                    "deaths": 4,
                    "assists": 6,
                    "totalMinionsKilled": 346,
                    "neutralMinionsKilled": 7,
                    "champLevel": 18,
                    "visionScore": 25,
                    "goldEarned": 18407,
                    "goldSpent": 18400,
                    "largestKillingSpree": 3,
                    "largestMultiKill": 1,
                    "doubleKills": 0,
                    "tripleKills": 0,
                    "quadraKills": 0,
                    "pentaKills": 0,
                    "totalDamageDealt": 280319,
                    "totalDamageDealtToChampions": 27389,
                    "physicalDamageDealtToChampions": 9287,
                    "magicDamageDealtToChampions": 17563,
                    "trueDamageDealtToChampions": 538,
                    "totalDamageTaken": 27478,
                    "damageSelfMitigated": 18924,
                    "largestCriticalStrike": 0,
                    "wardsPlaced": 12,
                    "wardsKilled": 3,
                    "visionWardsBoughtInGame": 0,
                    "turretKills": 1,
                    "inhibitorKills": 0,
                    "totalHeal": 4685,
                    "totalHealsOnTeammates": 0,
                    "totalDamageShieldedOnTeammates": 0,
                    "totalTimeCrowdControlDealt": 159,
                    "timeCCingOthers": 6,
                    "spell1Casts": 0,
                    "spell2Casts": 0,
                    "spell3Casts": 0,
                    "spell4Casts": 0,
                    "win": false
                  },
                  "timeline": {
                    "lane": "BOTTOM",
                    "role": "CARRY"
                  }
                },
                {
                  "participantId": 10,
                  "championId": 161,
                  "teamId": 200,
                  "stats": { "kills": 10, "deaths": 8, "assists": 9, "win": false },
                  "timeline": { "lane": "BOTTOM", "role": "SUPPORT" }
                }
              ]
            }
            """);

        var lcuClient = new FakeLcuClient(summaryMatch, detailMatch);
        var service = new MatchHistoryReconciliationService(
            lcuClient,
            scope.Games,
            scope.MissedGameDecisions,
            NullLogger<MatchHistoryReconciliationService>.Instance);

        var candidates = await service.FindMissedGamesAsync(checkGameSaved: static _ => false);

        var candidate = Assert.Single(candidates);
        Assert.Equal(5531339140, candidate.GameId);
        Assert.Equal(35, candidate.Stats.TeamKills);
        Assert.Equal(25.7, candidate.Stats.KillParticipation);
        Assert.Equal("Kaisa", candidate.Stats.ChampionName);
        Assert.Equal("chapy", candidate.Stats.SummonerName);
        Assert.Equal(1, lcuClient.MatchDetailsCalls);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeLcuClient : ILcuClient
    {
        private readonly JsonElement _summaryMatch;
        private readonly JsonElement _detailMatch;
        private readonly JsonElement _currentSummoner;

        public FakeLcuClient(JsonElement summaryMatch, JsonElement detailMatch)
        {
            _summaryMatch = summaryMatch;
            _detailMatch = detailMatch;
            _currentSummoner = ParseJson(
                """
                {
                  "displayName": "",
                  "gameName": "chapy"
                }
                """);
        }

        public int MatchDetailsCalls { get; private set; }

        public void Configure(LcuCredentials credentials)
        {
        }

        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(_currentSummoner);

        public Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default) =>
            Task.FromResult(GamePhase.None);

        public Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);

        public Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default) => Task.FromResult(420);

        public Task<List<JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default) =>
            Task.FromResult<List<JsonElement>>([_summaryMatch]);

        public Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default)
        {
            MatchDetailsCalls++;
            return Task.FromResult<JsonElement?>(_detailMatch);
        }

        public Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default) =>
            Task.FromResult<string?>(championId == 145 ? "Kaisa" : null);

        public Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);

        public Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(("", "", ""));
    }
}
