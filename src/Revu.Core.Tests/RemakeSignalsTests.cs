using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Lcu;
using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>3.14.1: only League's own early-surrender flag marks a remake.</summary>
public sealed class RemakeSignalsTests
{
    [Fact]
    public void Eog_TopLevelTrue_IsRemake()
    {
        var (remake, caused) = RemakeSignals.FromEog(Json("""{ "gameEndedInEarlySurrender": true, "localPlayer": { "stats": {} } }"""));
        Assert.True(remake);
        Assert.False(caused);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("\"true\"")]
    [InlineData("true")]
    public void Eog_StatsFlag_IsRemake(string raw)
    {
        var json = $$"""{ "localPlayer": { "stats": { "GAME_ENDED_IN_EARLY_SURRENDER": {{raw}} } } }""";
        Assert.True(RemakeSignals.FromEog(Json(json)).Remake);
    }

    [Fact]
    public void Eog_BothFalse_IsNotRemake()
    {
        var json = """{ "gameEndedInEarlySurrender": false, "localPlayer": { "stats": { "GAME_ENDED_IN_EARLY_SURRENDER": "0" } } }""";
        Assert.False(RemakeSignals.FromEog(Json(json)).Remake);
    }

    [Fact]
    public void Eog_BothMissing_IsUnknown()
    {
        Assert.Null(RemakeSignals.FromEog(Json("""{ "localPlayer": { "stats": { "WIN": "1" } } }""")).Remake);
        Assert.Null(RemakeSignals.FromEog(Json("""{ "gameEndedInEarlySurrender": "maybe" }""")).Remake);
    }

    [Theory]
    [InlineData("""{ "gameEndedInEarlySurrender": true, "localPlayer": { "leaver": false, "stats": { "WAS_AFK": "1" } } }""")]
    [InlineData("""{ "gameEndedInEarlySurrender": true, "localPlayer": { "leaver": true, "stats": { "WAS_AFK": "0" } } }""")]
    [InlineData("""{ "gameEndedInEarlySurrender": true, "causedEarlySurrender": true, "localPlayer": { "stats": {} } }""")]
    public void Eog_AfkOrLeaver_SetsCausedBySelf(string json)
    {
        var (remake, caused) = RemakeSignals.FromEog(Json(json));
        Assert.True(remake);
        Assert.True(caused);
    }

    [Fact]
    public void Eog_Describe_ListsFieldNamesAndValuesOnly()
    {
        var text = RemakeSignals.DescribeEog(Fixture("eog_remake.json"));
        Assert.Contains("gameEndedInEarlySurrender=true", text);
        Assert.Contains("stats.GAME_ENDED_IN_EARLY_SURRENDER=\"1\"", text);
        Assert.Contains("causedEarlySurrender=absent", text);
        Assert.DoesNotContain("Kai", text);
    }

    [Fact]
    public void LcuMatch_ReadsSelfThenFallsBackToAnyParticipant()
    {
        var self = Json("""
            { "participants": [
                { "participantId": 1, "puuid": "other", "stats": { "gameEndedInEarlySurrender": false } },
                { "participantId": 2, "puuid": "me", "stats": { "gameEndedInEarlySurrender": true } } ] }
            """);
        Assert.True(RemakeSignals.FromLcuMatch(self, "me"));

        var fallback = Json("""
            { "participants": [
                { "participantId": 1, "stats": { "kills": 0 } },
                { "participantId": 2, "stats": { "gameEndedInEarlySurrender": true } } ] }
            """);
        Assert.True(RemakeSignals.FromLcuMatch(fallback, "me"));

        var legacy = Json("""
            { "participantIdentities": [ { "participantId": 3, "player": { "currentPlayer": true } } ],
              "participants": [
                { "participantId": 1, "stats": { "gameEndedInEarlySurrender": true } },
                { "participantId": 3, "stats": { "gameEndedInEarlySurrender": false } } ] }
            """);
        Assert.False(RemakeSignals.FromLcuMatch(legacy, ""));
    }

    [Fact]
    public void LcuMatch_Missing_IsUnknown()
    {
        Assert.Null(RemakeSignals.FromLcuMatch(Json("""{ "participants": [ { "stats": { "kills": 1 } } ] }"""), "me"));
        Assert.Null(RemakeSignals.FromLcuMatch(Json("""{ "gameId": 1 }"""), "me"));
    }

    [Fact]
    public void MatchV5_TrueWithBlankPositions()
    {
        Assert.True(RemakeSignals.FromMatchV5(Fixture("matchv5_remake.json"), "self-puuid"));
        Assert.True(RemakeSignals.FromMatchV5(Fixture("matchv5_remake.json"), "not-in-match"));
    }

    [Fact]
    public void MatchV5_FalseAndMissing()
    {
        var normal = Json("""
            { "info": { "participants": [ { "puuid": "me", "teamPosition": "MIDDLE", "gameEndedInEarlySurrender": false } ] } }
            """);
        Assert.False(RemakeSignals.FromMatchV5(normal, "me"));
        Assert.Null(RemakeSignals.FromMatchV5(Json("""{ "info": { "participants": [ { "puuid": "me" } ] } }"""), "me"));
        Assert.Null(RemakeSignals.FromMatchV5(Json("""{ "metadata": {} }"""), "me"));
    }

    [Fact]
    public void Extractor_EogRemakeFixture_CarriesTheFlagAndSource()
    {
        var stats = StatsExtractor.ExtractFromEog(Fixture("eog_remake.json"), NullLogger.Instance);
        Assert.NotNull(stats);
        Assert.True(stats!.EndedInEarlySurrender);
        Assert.False(stats.CausedEarlySurrender);
        Assert.Equal("eog", stats.EarlySurrenderSource);
        Assert.Equal("1", stats.RawStats["_early_surrender"]);
        Assert.Equal(212, stats.GameDuration);
    }

    [Fact]
    public void Extractor_EogSelfAfkFixture_MarksCausedBySelf()
    {
        var stats = StatsExtractor.ExtractFromEog(Fixture("eog_remake_self_afk.json"), NullLogger.Instance);
        Assert.NotNull(stats);
        Assert.True(stats!.EndedInEarlySurrender);
        Assert.True(stats.CausedEarlySurrender);
    }

    [Fact]
    public void Extractor_NormalFixtures_HaveNoFlag()
    {
        var eog = StatsExtractor.ExtractFromEog(Fixture("eog_ranked_kai_sa.json"), NullLogger.Instance);
        Assert.Null(eog!.EndedInEarlySurrender);
        Assert.False(eog.RawStats.ContainsKey("_early_surrender"));

        var history = StatsExtractor.ExtractFromMatchHistory(Fixture("match_history_ranked_ahri.json"), NullLogger.Instance);
        Assert.Null(history!.EndedInEarlySurrender);
        Assert.Equal("lcu", history.EarlySurrenderSource);
    }
}
