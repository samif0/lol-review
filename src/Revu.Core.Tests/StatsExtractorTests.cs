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
}
