using System.Text.Json;
using LoLReview.Core.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoLReview.Core.Tests;

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
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
