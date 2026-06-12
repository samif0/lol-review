using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class TimelineLaningExtractorTests
{
    private const string SelfPuuid = "self-puuid";
    private const string OpponentPuuid = "opp-puuid";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement MatchPayload() => Parse($$"""
        {
          "info": {
            "participants": [
              { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "MIDDLE", "championName": "Ahri" },
              { "puuid": "{{OpponentPuuid}}", "participantId": 6, "teamId": 200, "teamPosition": "MIDDLE", "championName": "Zed" }
            ]
          }
        }
        """);

    private static JsonElement TimelinePayload(long frameTimestampMs) => Parse($$"""
        {
          "metadata": { "participants": ["{{SelfPuuid}}", "x2", "x3", "x4", "x5", "{{OpponentPuuid}}", "x7", "x8", "x9", "x10"] },
          "info": {
            "frames": [
              { "timestamp": 0, "participantFrames": {
                  "1": { "minionsKilled": 0, "jungleMinionsKilled": 0, "totalGold": 500 },
                  "6": { "minionsKilled": 0, "jungleMinionsKilled": 0, "totalGold": 500 } } },
              { "timestamp": {{frameTimestampMs}}, "participantFrames": {
                  "1": { "minionsKilled": 68, "jungleMinionsKilled": 4, "totalGold": 3950 },
                  "6": { "minionsKilled": 61, "jungleMinionsKilled": 0, "totalGold": 3610 } } }
            ]
          }
        }
        """);

    [Fact]
    public void Extract_ComputesCsAndDiffsAtTenMinutes()
    {
        var result = TimelineLaningExtractor.Extract(MatchPayload(), TimelinePayload(600_000), SelfPuuid);

        Assert.NotNull(result);
        Assert.Equal(72, result!.CsAt10);
        Assert.Equal(340, result.GoldDiffAt10);
        Assert.Equal(11, result.CsDiffAt10);
    }

    [Fact]
    public void Extract_ReturnsNullWhenGameShorterThanTenMinutes()
    {
        // Last frame at 8 minutes — remake / early surrender, no 10-min mark.
        var result = TimelineLaningExtractor.Extract(MatchPayload(), TimelinePayload(480_000), SelfPuuid);

        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsCsWithoutDiffsWhenOpponentUnresolvable()
    {
        // Match payload without teamPosition (e.g. ARAM) — opponent unknown.
        var match = Parse($$"""
            {
              "info": {
                "participants": [
                  { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "", "championName": "Ahri" }
                ]
              }
            }
            """);

        var result = TimelineLaningExtractor.Extract(match, TimelinePayload(600_000), SelfPuuid);

        Assert.NotNull(result);
        Assert.Equal(72, result!.CsAt10);
        Assert.Null(result.GoldDiffAt10);
        Assert.Null(result.CsDiffAt10);
    }

    [Fact]
    public void Extract_ReturnsNullWhenPlayerNotInTimeline()
    {
        var result = TimelineLaningExtractor.Extract(MatchPayload(), TimelinePayload(600_000), "someone-else");

        Assert.Null(result);
    }

    [Fact]
    public async Task Repository_RoundTripsLaningColumns()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var saved = await scope.Games.SaveAsync(new Revu.Core.Models.GameStats
        {
            GameId = 920_001,
            Timestamp = now,
            QueueType = "Ranked Solo/Duo",
            ChampionName = "Orianna",
            Win = true,
            GameDuration = 1900,
        });
        Assert.True(saved >= 0);

        var missing = await scope.Games.GetGameIdsMissingLaningAsync();
        Assert.Contains(920_001, missing);

        await scope.Games.UpdateLaningAt10Async(920_001, csAt10: 72, goldDiffAt10: 340, csDiffAt10: 11);

        missing = await scope.Games.GetGameIdsMissingLaningAsync();
        Assert.DoesNotContain(920_001, missing);

        var game = await scope.Games.GetAsync(920_001);
        Assert.NotNull(game);
        Assert.Equal(72, game!.CsAt10);
        Assert.Equal(340, game.GoldDiffAt10);
        Assert.Equal(11, game.CsDiffAt10);
    }
}
