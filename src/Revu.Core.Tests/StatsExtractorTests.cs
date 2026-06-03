using System.Text.Json;
using Revu.Core.Lcu;
using Revu.Core.Models;
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
