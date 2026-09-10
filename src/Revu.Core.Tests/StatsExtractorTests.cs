using System.Text.Json;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class StatsExtractorTests
{
    [Fact]
    public void ExtractFromEog_UsesTeamStatsForKillParticipationWhenPlayerRowsAreIncomplete()
    {
        var eog = ParseJson(
            """
            {
              "gameId": 5531387189,
              "gameLength": 1662,
              "gameMode": "CLASSIC",
              "queueType": "RANKED_SOLO_5x5",
              "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": 100,
                "championName": "Kai'Sa",
                "championId": 145,
                "selectedPosition": "BOTTOM",
                "stats": {
                  "CHAMPIONS_KILLED": "14",
                  "NUM_DEATHS": "0",
                  "ASSISTS": "3",
                  "MINIONS_KILLED": "266",
                  "NEUTRAL_MINIONS_KILLED": "0",
                  "WIN": "1"
                }
              },
              "teams": [
                {
                  "teamId": 100,
                  "stats": {
                    "CHAMPIONS_KILLED": 27
                  },
                  "players": [
                    {
                      "championName": "Kai'Sa",
                      "selectedPosition": "BOTTOM",
                      "stats": {
                        "CHAMPIONS_KILLED": 14
                      }
                    }
                  ]
                },
                {
                  "teamId": 200,
                  "players": []
                }
              ]
            }
            """);

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal(27, stats!.TeamKills);
        Assert.Equal(63.0, stats.KillParticipation);
        Assert.Equal("Ranked Solo/Duo", stats.GameMode);
        Assert.Equal("Ranked Solo/Duo", stats.QueueType);
        Assert.Equal("Ranked Solo/Duo", stats.DisplayGameMode);
    }

    /// <summary>
    /// v2.17.25: when selectedPosition is present, BOTTOM→Bot and UTILITY→Supp,
    /// so the bot lane is keyed correctly (support under ownSupp, ADC under
    /// ownBot) — the data behind the "Champ+Supp vs Champ+Supp" pairing pill.
    /// </summary>
    [Fact]
    public void ExtractFromEog_MapsBotLaneByPosition_NotSlotOrder()
    {
        // Deliberately list the SUPPORT before the ADC in the players array so a
        // slot-index fallback (old bug) would mis-key them. With positions present
        // the map must still be correct.
        var eog = ParseJson(
            """
            {
              "gameId": 5531387000,
              "gameLength": 1600,
              "gameMode": "CLASSIC",
              "queueType": "RANKED_SOLO_5x5",
              "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": 100, "championName": "Nautilus", "championId": 111,
                "selectedPosition": "UTILITY",
                "stats": { "CHAMPIONS_KILLED": "1", "NUM_DEATHS": "5", "ASSISTS": "20", "WIN": "1" }
              },
              "teams": [
                {
                  "teamId": 100, "stats": { "CHAMPIONS_KILLED": 30 },
                  "players": [
                    { "championName": "Nautilus", "selectedPosition": "UTILITY", "stats": { "CHAMPIONS_KILLED": 1 } },
                    { "championName": "Sivir",    "selectedPosition": "BOTTOM",  "stats": { "CHAMPIONS_KILLED": 12 } }
                  ]
                },
                {
                  "teamId": 200, "stats": { "CHAMPIONS_KILLED": 20 },
                  "players": [
                    { "championName": "Renata Glasc", "selectedPosition": "UTILITY", "stats": { "CHAMPIONS_KILLED": 0 } },
                    { "championName": "Varus",        "selectedPosition": "BOTTOM",  "stats": { "CHAMPIONS_KILLED": 9 } }
                  ]
                }
              ]
            }
            """);

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance);

        Assert.NotNull(stats);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats!.ParticipantMap);
        Assert.NotNull(map);
        Assert.Equal("Nautilus", map!["ownSupp"]);
        Assert.Equal("Sivir", map["ownBot"]);
        Assert.Equal("Renata Glasc", map["enemySupp"]);
        Assert.Equal("Varus", map["enemyBot"]);
    }

    /// <summary>
    /// v2.17.25: when selectedPosition is blank we must NOT guess bot/supp from
    /// slot order. The bot/supp keys are omitted so the matchup pill falls back to
    /// lane-only instead of a confidently-wrong "ADC vs ADC" pairing.
    /// </summary>
    [Fact]
    public void ExtractFromEog_OmitsBotSuppKeys_WhenPositionsMissing()
    {
        var eog = ParseJson(
            """
            {
              "gameId": 5531387001,
              "gameLength": 1600,
              "gameMode": "CLASSIC",
              "queueType": "RANKED_SOLO_5x5",
              "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": 100, "championName": "Sivir", "championId": 15,
                "selectedPosition": "",
                "stats": { "CHAMPIONS_KILLED": "10", "NUM_DEATHS": "4", "ASSISTS": "8", "WIN": "1" }
              },
              "teams": [
                {
                  "teamId": 100, "stats": { "CHAMPIONS_KILLED": 30 },
                  "players": [
                    { "championName": "Sivir",    "selectedPosition": "", "stats": { "CHAMPIONS_KILLED": 10 } },
                    { "championName": "Nautilus", "selectedPosition": "", "stats": { "CHAMPIONS_KILLED": 1 } }
                  ]
                },
                {
                  "teamId": 200, "stats": { "CHAMPIONS_KILLED": 20 },
                  "players": [
                    { "championName": "Varus",        "selectedPosition": "", "stats": { "CHAMPIONS_KILLED": 9 } },
                    { "championName": "Renata Glasc", "selectedPosition": "", "stats": { "CHAMPIONS_KILLED": 0 } }
                  ]
                }
              ]
            }
            """);

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance);

        Assert.NotNull(stats);
        // No positions → no role keys we can trust → empty map (lane-only fallback).
        if (!string.IsNullOrEmpty(stats!.ParticipantMap))
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats.ParticipantMap);
            Assert.NotNull(map);
            Assert.False(map!.ContainsKey("ownBot"), "ownBot must not be guessed from slot order");
            Assert.False(map.ContainsKey("ownSupp"), "ownSupp must not be guessed from slot order");
            Assert.False(map.ContainsKey("enemyBot"), "enemyBot must not be guessed from slot order");
            Assert.False(map.ContainsKey("enemySupp"), "enemySupp must not be guessed from slot order");
        }
    }

    /// <summary>
    /// v3.10: the LCU's end-of-game payload often leaves selectedPosition blank
    /// while detectedTeamPosition is populated (observed on every ranked game for
    /// days). That is still an assigned position — never a slot guess — so the
    /// matchup (enemy laner + role map) must come out of the payload itself
    /// instead of waiting for the Match-V5 heal.
    /// </summary>
    [Fact]
    public void ExtractFromEog_FallsBackToDetectedTeamPosition_WhenSelectedPositionIsBlank()
    {
        var eog = ParseJson(
            """
            {
              "gameId": 5638993644,
              "gameLength": 1545,
              "gameMode": "CLASSIC",
              "queueType": "RANKED_SOLO_5x5",
              "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": 200, "championName": "Qiyana", "championId": 246,
                "selectedPosition": "", "detectedTeamPosition": "MIDDLE",
                "stats": { "CHAMPIONS_KILLED": "8", "NUM_DEATHS": "3", "ASSISTS": "6", "WIN": "1" }
              },
              "teams": [
                {
                  "teamId": 100, "stats": { "CHAMPIONS_KILLED": 14 },
                  "players": [
                    { "championName": "Sylas", "selectedPosition": "", "detectedTeamPosition": "MIDDLE", "stats": { "CHAMPIONS_KILLED": 5 } },
                    { "championName": "Viego", "selectedPosition": "", "detectedTeamPosition": "JUNGLE", "stats": { "CHAMPIONS_KILLED": 4 } }
                  ]
                },
                {
                  "teamId": 200, "stats": { "CHAMPIONS_KILLED": 22 },
                  "players": [
                    { "championName": "Qiyana", "selectedPosition": "", "detectedTeamPosition": "MIDDLE", "stats": { "CHAMPIONS_KILLED": 8 } },
                    { "championName": "Lee Sin", "selectedPosition": "", "detectedTeamPosition": "JUNGLE", "stats": { "CHAMPIONS_KILLED": 6 } }
                  ]
                }
              ]
            }
            """);

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("MIDDLE", stats!.Position);
        Assert.Equal("Sylas", stats.EnemyLaner);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats.ParticipantMap);
        Assert.NotNull(map);
        Assert.Equal("Qiyana", map!["ownMid"]);
        Assert.Equal("Lee Sin", map["ownJg"]);
        Assert.Equal("Sylas", map["enemyMid"]);
        Assert.Equal("Viego", map["enemyJg"]);
    }

    [Fact]
    public void ExtractFromEog_PlaceholderSelectedPosition_ReadsAsBlank_AndFallsBackToDetected()
    {
        // "NONE" on both the local player and an enemy must not pair them as lane
        // opponents; the client's detected lane is the assignment.
        var eog = ParseJson(
            """
            {
              "gameId": 5638993645, "gameLength": 1500, "gameMode": "CLASSIC",
              "queueType": "RANKED_SOLO_5x5", "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": 100, "championName": "Sivir", "championId": 15,
                "selectedPosition": "NONE", "detectedTeamPosition": "BOTTOM",
                "stats": { "CHAMPIONS_KILLED": "10", "NUM_DEATHS": "4", "ASSISTS": "8", "WIN": "1" }
              },
              "teams": [
                { "teamId": 100, "stats": { "CHAMPIONS_KILLED": 30 }, "players": [
                    { "championName": "Sivir", "selectedPosition": "NONE", "detectedTeamPosition": "BOTTOM", "stats": { "CHAMPIONS_KILLED": 10 } } ] },
                { "teamId": 200, "stats": { "CHAMPIONS_KILLED": 20 }, "players": [
                    { "championName": "Malphite", "selectedPosition": "NONE", "detectedTeamPosition": "TOP", "stats": { "CHAMPIONS_KILLED": 2 } },
                    { "championName": "Varus", "selectedPosition": "NONE", "detectedTeamPosition": "BOTTOM", "stats": { "CHAMPIONS_KILLED": 9 } } ] }
              ]
            }
            """);

        var stats = StatsExtractor.ExtractFromEog(eog, NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("BOTTOM", stats!.Position);
        Assert.Equal("Varus", stats.EnemyLaner);
    }

    [Fact]
    public void ExtractFromMatchHistory_PrefersQueueLabelForRankedSoloDuo()
    {
        var match = ParseJson(
            """
            {
              "gameId": 5531387190,
              "gameCreation": 1710000000000,
              "gameDuration": 1800,
              "gameMode": "CLASSIC",
              "gameType": "MATCHED_GAME",
              "queueId": 420,
              "participantIdentities": [
                {
                  "participantId": 1,
                  "player": {
                    "currentPlayer": true
                  }
                }
              ],
              "participants": [
                {
                  "participantId": 1,
                  "teamId": 100,
                  "championId": 103,
                  "championName": "Ahri",
                  "spell1Id": 4,
                  "spell2Id": 14,
                  "stats": {
                    "win": true,
                    "kills": 7,
                    "deaths": 3,
                    "assists": 8,
                    "totalMinionsKilled": 180,
                    "neutralMinionsKilled": 12
                  }
                }
              ]
            }
            """);

        var stats = StatsExtractor.ExtractFromMatchHistory(match, NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("Ranked Solo/Duo", stats!.GameMode);
        Assert.Equal("Ranked Solo/Duo", stats.QueueType);
        Assert.Equal("Ranked Solo/Duo", stats.DisplayGameMode);
    }

    // ── v3.9.2: match-history bot lane — the timeline ROLE separates ADC from support ──
    // The LCU match-history payload has no per-player position, only timeline
    // lane + role. BOTTOM alone can't tell the ADC from the support, so the
    // extractor used to drop both from the participant map — a recovered
    // bot-lane game had no opponents at all (the Matchups "new card from last
    // game" button went dark for bot/support players). EnemyLaner stays blank
    // on this path on purpose: it is what keeps the row in the Match-V5
    // backfill sweep so the heuristic map gets replaced by teamPosition data.

    private static JsonElement MatchHistoryPayload(int currentParticipantId, params (int Id, int Team, string Champ, string Lane, string Role)[] rows)
    {
        var identities = string.Join(",", rows.Select(r =>
            $$$"""{"participantId":{{{r.Id}}},"player":{"currentPlayer":{{{(r.Id == currentParticipantId ? "true" : "false")}}}}}"""));
        var participants = string.Join(",", rows.Select(r =>
            $$$"""{"participantId":{{{r.Id}}},"teamId":{{{r.Team}}},"championId":1,"championName":"{{{r.Champ}}}","stats":{"win":true,"kills":1,"deaths":1,"assists":1},"timeline":{"lane":"{{{r.Lane}}}","role":"{{{r.Role}}}"}}"""));
        return ParseJson($$$"""
            {
              "gameId": 5531387195, "gameCreation": 1710000000000, "gameDuration": 1800,
              "gameMode": "CLASSIC", "gameType": "MATCHED_GAME", "queueId": 420,
              "participantIdentities": [{{{identities}}}],
              "participants": [{{{participants}}}]
            }
            """);
    }

    private static (int, int, string, string, string)[] FullLobby(string ownAdcRole = "DUO_CARRY", string ownSuppRole = "DUO_SUPPORT") =>
    [
        (1, 100, "Aatrox", "TOP", "SOLO"), (2, 100, "Lee Sin", "JUNGLE", "NONE"), (3, 100, "Ahri", "MIDDLE", "SOLO"),
        (4, 100, "Kai'Sa", "BOTTOM", ownAdcRole), (5, 100, "Nautilus", "BOTTOM", ownSuppRole),
        (6, 200, "Sett", "TOP", "SOLO"), (7, 200, "Graves", "JUNGLE", "NONE"), (8, 200, "Syndra", "MIDDLE", "SOLO"),
        (9, 200, "Tristana", "BOTTOM", "DUO_CARRY"), (10, 200, "Renata", "BOTTOM", "DUO_SUPPORT"),
    ];

    [Fact]
    public void ExtractFromMatchHistory_BotLaneAdc_GetsBottomPositionBotSupportKeysAndTheEnemyAdc()
    {
        var stats = StatsExtractor.ExtractFromMatchHistory(MatchHistoryPayload(4, FullLobby()), NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("Kai'Sa", stats!.ChampionName);
        Assert.Equal("BOTTOM", stats.Position);
        Assert.Equal("", stats.EnemyLaner); // left to the Match-V5 backfill (see above)
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats.ParticipantMap)!;
        Assert.Equal("Kai'Sa", map["ownBot"]);
        Assert.Equal("Nautilus", map["ownSupp"]);
        Assert.Equal("Tristana", map["enemyBot"]);
        Assert.Equal("Renata", map["enemySupp"]);
        Assert.Equal("Ahri", map["ownMid"]);
        Assert.Equal("Graves", map["enemyJg"]);
        Assert.Equal(10, map.Count);
        // And the journal can pre-fill the full 2v2 from it.
        var prefill = MatchupPrefill.FromGame(stats);
        Assert.NotNull(prefill);
        Assert.True(prefill!.IsComplete);
        Assert.Equal("bot", prefill.Lane);
        Assert.Equal(new[] { "Kai'Sa", "Nautilus" }, prefill.AllyChamps);
        Assert.Equal(new[] { "Tristana", "Renata Glasc" }, prefill.EnemyChamps);
    }

    [Fact]
    public void ExtractFromMatchHistory_BotLaneSupport_GetsUtilityPositionAndTheEnemySupport()
    {
        var stats = StatsExtractor.ExtractFromMatchHistory(MatchHistoryPayload(5, FullLobby()), NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("Nautilus", stats!.ChampionName);
        // Same position the live EOG capture stores for a support.
        Assert.Equal("UTILITY", stats.Position);
        Assert.Equal("", stats.EnemyLaner);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats.ParticipantMap)!;
        Assert.Equal("Nautilus", map["ownSupp"]);
        Assert.Equal("Renata", map["enemySupp"]);
        var prefill = MatchupPrefill.FromGame(stats)!;
        Assert.Equal("support", prefill.Lane);
        Assert.Equal(new[] { "Kai'Sa", "Nautilus" }, prefill.AllyChamps);
        Assert.Equal(new[] { "Tristana", "Renata Glasc" }, prefill.EnemyChamps);
    }

    [Fact]
    public void ExtractFromMatchHistory_IndefiniteBotRoles_AreStillOmitted_NeverGuessed()
    {
        // Riot's heuristic couldn't settle the duo: both bot-laners report role DUO.
        var stats = StatsExtractor.ExtractFromMatchHistory(MatchHistoryPayload(4, FullLobby("DUO", "DUO")), NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("BOTTOM", stats!.Position); // the lane, unchanged
        Assert.Equal("", stats.EnemyLaner);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats.ParticipantMap)!;
        Assert.False(map.ContainsKey("ownBot"));
        Assert.False(map.ContainsKey("ownSupp"));
        Assert.Equal("Tristana", map["enemyBot"]); // the enemy duo WAS definite
        Assert.Equal("Ahri", map["ownMid"]);
    }

    [Fact]
    public void ExtractFromMatchHistory_MidLane_LeavesTheOpponentToTheBackfill_ButTheMapHasIt()
    {
        var stats = StatsExtractor.ExtractFromMatchHistory(MatchHistoryPayload(3, FullLobby()), NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal("MIDDLE", stats!.Position);
        Assert.Equal("", stats.EnemyLaner);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stats.ParticipantMap)!;
        Assert.Equal("Syndra", map["enemyMid"]);
        Assert.Equal(new[] { "Syndra" }, MatchupPrefill.FromGame(stats)!.EnemyChamps);
    }

    [Theory]
    [InlineData("eog_ranked_kai_sa.json", "eog", 5531387189, "Kai'Sa", "Ranked Solo/Duo")]
    [InlineData("match_history_ranked_ahri.json", "match", 5531387190, "Ahri", "Ranked Solo/Duo")]
    public void Extractors_ParseFixturePayloads(
        string fileName,
        string source,
        long expectedGameId,
        string expectedChampion,
        string expectedQueue)
    {
        var payload = ParseJson(File.ReadAllText(GetFixturePath(fileName)));
        var stats = source == "eog"
            ? StatsExtractor.ExtractFromEog(payload, NullLogger.Instance)
            : StatsExtractor.ExtractFromMatchHistory(payload, NullLogger.Instance);

        Assert.NotNull(stats);
        Assert.Equal(expectedGameId, stats!.GameId);
        Assert.Equal(expectedChampion, stats.ChampionName);
        Assert.Equal(expectedQueue, stats.QueueType);
        Assert.True(stats.KillParticipation >= 0);
    }

    [Theory]
    [InlineData("CLASSIC", "420", "Ranked Solo/Duo")]
    [InlineData("Ranked Solo", "420", "Ranked Solo/Duo")]
    [InlineData("CLASSIC", "Ranked Flex", "Ranked Flex")]
    [InlineData("CLASSIC", "", "Ranked Solo/Duo")]
    public void DisplayGameMode_NormalizesLegacyModes(string gameMode, string queueType, string expected)
    {
        var stats = new GameStats
        {
            GameMode = gameMode,
            QueueType = queueType,
        };

        Assert.Equal(expected, stats.DisplayGameMode);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string GetFixturePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Revu.Core.Tests",
                "Fixtures",
                "StatsExtractor",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate StatsExtractor fixture {fileName}");
    }
}
