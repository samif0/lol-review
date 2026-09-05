using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pure unit tests for <see cref="TeamfightAnalyzer"/>: fights from the Match-V5 kill
/// events (time + space clustering), the NUMBERS at the player's commitment instant,
/// self involvement, clock alignment, death stamps, and the mode gate. Payload shapes
/// mirror Riot's match + timeline documents.
/// </summary>
public sealed class TeamfightAnalyzerTests
{
    private const string SelfPuuid = "self-puuid";

    // Self = 1 (mid, team 100) with allies 2 (jungle) and 3 (bot); enemies 6, 7, 8.
    private static JsonElement MatchPayload(int mapId = 11, string mode = "CLASSIC") => Parse($$"""
        { "info": { "mapId": {{mapId}}, "gameMode": "{{mode}}", "participants": [
            { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "MIDDLE", "championName": "Ahri" },
            { "puuid": "p2", "participantId": 2, "teamId": 100, "teamPosition": "JUNGLE", "championName": "LeeSin" },
            { "puuid": "p3", "participantId": 3, "teamId": 100, "teamPosition": "BOTTOM", "championName": "Jinx" },
            { "puuid": "p6", "participantId": 6, "teamId": 200, "teamPosition": "MIDDLE", "championName": "Zed" },
            { "puuid": "p7", "participantId": 7, "teamId": 200, "teamPosition": "JUNGLE", "championName": "Nocturne" },
            { "puuid": "p8", "participantId": 8, "teamId": 200, "teamPosition": "TOP", "championName": "Garen" }
        ] } }
        """);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Timeline(params string[] events) => Parse($$"""
        { "info": { "frames": [ { "timestamp": 0, "participantFrames": {}, "events": [] },
                                { "timestamp": 900000, "participantFrames": {}, "events": [ {{string.Join(",", events)}} ] } ] } }
        """);

    private static string Kill(int s, int killer, int victim, int x = 7000, int y = 7000,
        int[]? assists = null, int[]? dealtTo = null, int[]? receivedFrom = null) => $$"""
        { "type": "CHAMPION_KILL", "timestamp": {{s * 1000}}, "killerId": {{killer}}, "victimId": {{victim}},
          "assistingParticipantIds": [{{string.Join(",", assists ?? [])}}],
          "victimDamageDealt": [{{string.Join(",", (dealtTo ?? []).Select(p => $$"""{ "participantId": {{p}} }"""))}}],
          "victimDamageReceived": [{{string.Join(",", (receivedFrom ?? []).Select(p => $$"""{ "participantId": {{p}} }"""))}}],
          "position": { "x": {{x}}, "y": {{y}} } }
        """;

    private static string LevelUp(int s, int pid, int level) =>
        $$"""{ "type": "LEVEL_UP", "timestamp": {{s * 1000}}, "participantId": {{pid}}, "level": {{level}} }""";

    private static GameEvent Ev(string type, int t, string details = "{}", int id = 0) =>
        new() { Id = id, EventType = type, GameTimeS = t, Details = details };

    private static JsonElement D(GameEvent e) => Parse(e.Details);

    // ── detection ────────────────────────────────────────────────────────────

    [Fact]
    public void ThreeKillCluster_EmitsOneOwnFight_WithNumbersAtEntry()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6, assists: [2]),
            Kill(605, killer: 7, victim: 3),
            Kill(610, killer: 2, victim: 7, assists: [1]));

        var result = TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        var fight = Assert.Single(result.Fights);
        Assert.Equal("TEAMFIGHT", fight.EventType);
        Assert.Equal(600, fight.GameTimeS);
        var d = D(fight);
        Assert.Equal("in", d.GetProperty("self").GetString());
        Assert.Equal(600, d.GetProperty("start_s").GetInt32());
        Assert.Equal(610, d.GetProperty("end_s").GetInt32());
        Assert.Equal(600, d.GetProperty("entry_s").GetInt32());
        // At the entry kill only Ahri, Lee Sin and Zed are on the feed: 2v1, numbers up.
        Assert.Equal("2v1", d.GetProperty("numbers").GetString());
        Assert.Equal("up", d.GetProperty("verdict").GetString());
        Assert.Equal(1, d.GetProperty("delta").GetInt32());
        // Over the whole fight everyone shows up: 3v2.
        Assert.Equal("3v2", d.GetProperty("became").GetString());
        Assert.Equal(3, d.GetProperty("kills").GetInt32());
        Assert.Equal(2, d.GetProperty("kills_for").GetInt32());
        Assert.Equal(1, d.GetProperty("kills_against").GetInt32());
        Assert.Equal("won", d.GetProperty("outcome").GetString());
        Assert.Equal("2v1", d.GetProperty("filters").GetProperty("numbers").GetProperty("label").GetString());
        Assert.Equal(1, d.GetProperty("filters").GetProperty("numbers").GetProperty("favor").GetInt32());
        Assert.Equal(new[] { "Ahri", "LeeSin" }, d.GetProperty("ally_champions").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "Zed" }, d.GetProperty("enemy_champions").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True(d.GetProperty("detected").GetBoolean());
        Assert.False(d.TryGetProperty("source", out _)); // never a reviewed-encounter shape
        Assert.Equal(new[] { "NUMBERS_UP_TEAMFIGHT", "TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(fight));
    }

    [Fact]
    public void WalkIntoOneVThree_ThenAlliesCleanUp_ReadsDownAtEntry_AndStampsTheDeath()
    {
        // The fight the objective exists to catch: the player dies first to three, the
        // team arrives afterwards and trades back. Whole-window numbers would say 3v3.
        var timeline = Timeline(
            Kill(600, killer: 6, victim: 1, assists: [7, 8]),
            Kill(606, killer: 2, victim: 6),
            Kill(609, killer: 3, victim: 7, assists: [2]));
        var death = Ev("DEATH", 600, "{\"killer\":\"Zed\"}", id: 41);

        var result = TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        var d = D(Assert.Single(result.Fights));
        Assert.Equal("1v3", d.GetProperty("numbers").GetString());
        Assert.Equal("down", d.GetProperty("verdict").GetString());
        Assert.Equal("3v3", d.GetProperty("became").GetString());
        Assert.Equal(-1, d.GetProperty("filters").GetProperty("numbers").GetProperty("favor").GetInt32());
        Assert.Equal(new[] { "OUTNUMBERED_TEAMFIGHT", "TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(Assert.Single(result.Fights)));
    }

    [Fact]
    public void FightOutcome_CountsKillsFromTheTeamsPerspective()
    {
        var timeline = Timeline(
            Kill(600, killer: 6, victim: 1, assists: [7, 8]),
            Kill(606, killer: 2, victim: 6),
            Kill(609, killer: 3, victim: 7, assists: [2]));

        var d = D(Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights));

        Assert.Equal(2, d.GetProperty("kills_for").GetInt32());
        Assert.Equal(1, d.GetProperty("kills_against").GetInt32());
        Assert.Equal("won", d.GetProperty("outcome").GetString());
    }

    [Fact]
    public void DeathInsideOwnFight_IsStampedWithTheFightNumbers()
    {
        var timeline = Timeline(
            Kill(600, killer: 6, victim: 1, assists: [7, 8]),
            Kill(606, killer: 2, victim: 6),
            Kill(609, killer: 3, victim: 7, assists: [2]));
        var death = Ev("DEATH", 601, "{\"killer\":\"Zed\",\"jungle_gank\":true}", id: 41);
        var lone = Ev("DEATH", 1500, "{\"killer\":\"Garen\"}", id: 42);

        var result = TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death, lone]);

        var stamped = Assert.Single(result.StampedDeaths);
        Assert.Same(death, stamped);
        var d = D(death);
        Assert.Equal("1v3", d.GetProperty("fight_numbers").GetString());
        Assert.Equal("down", d.GetProperty("fight_verdict").GetString());
        Assert.Equal(600, d.GetProperty("fight_start_s").GetInt32());
        Assert.True(d.GetProperty("jungle_gank").GetBoolean()); // existing keys preserved
        Assert.Equal("{\"killer\":\"Garen\"}", lone.Details);
    }

    [Fact]
    public void SimultaneousFightsAcrossMap_SplitIntoTwoFights()
    {
        // Bot side (self) and top side (allies) trade kills within seconds of each
        // other. Time alone would merge them into one 3v3; space keeps them apart.
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6, x: 2000, y: 2000),
            Kill(601, killer: 2, victim: 7, x: 12000, y: 12000),
            Kill(604, killer: 8, victim: 1, x: 2100, y: 2050),
            Kill(605, killer: 3, victim: 8, x: 12100, y: 12000),
            Kill(608, killer: 6, victim: 3, x: 2000, y: 2100, assists: [8]),
            Kill(609, killer: 2, victim: 6, x: 12000, y: 12100));

        var result = TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        Assert.Equal(2, result.Fights.Count);
        var own = Assert.Single(result.Fights, f => D(f).GetProperty("self").GetString() == "in");
        var away = Assert.Single(result.Fights, f => D(f).GetProperty("self").GetString() == "away");
        Assert.Equal(3, D(own).GetProperty("kills").GetInt32());
        Assert.Equal(3, D(away).GetProperty("kills").GetInt32());
        Assert.Equal(new[] { "ABSENT_TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(away));
    }

    [Fact]
    public void KillsMoreThanOneGapApart_DoNotFormAFight()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(620, killer: 1, victim: 7),
            Kill(640, killer: 1, victim: 8));

        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights);
    }

    [Fact]
    public void TwoKillCluster_IsAFight_OnlyWhenTheOwnEventClusterSaysSo()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(606, killer: 1, victim: 7));

        // No stored events → no synthetic cluster → two kills stay below the floor.
        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights);

        // KILL + MULTI_KILL + KILL is a fight today; it must stay one after the pass.
        var stored = new[] { Ev("KILL", 600), Ev("KILL", 606), Ev("MULTI_KILL", 606) };
        var fight = Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, stored).Fights);
        Assert.Equal(2, D(fight).GetProperty("kills").GetInt32());
        Assert.Equal("in", D(fight).GetProperty("self").GetString());
    }

    [Fact]
    public void LoneExecute_NeverCountsAsAKill()
    {
        // A minion execute between two kills would bridge them into a three-kill
        // cluster; it is not a fight signal and must be ignored.
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(605, killer: 0, victim: 2),
            Kill(610, killer: 7, victim: 3));

        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights);
    }

    [Fact]
    public void TowerDiveExecute_WithAssisters_StillCounts()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(605, killer: 0, victim: 3, assists: [7]),
            Kill(610, killer: 7, victim: 2));

        var fight = Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights);
        Assert.Equal(3, D(fight).GetProperty("kills").GetInt32());
    }

    // ── numbers ─────────────────────────────────────────────────────────────

    [Fact]
    public void DamageExchangeWithTheVictim_CountsAsCredit()
    {
        // Jinx hit Zed before Ahri finished him, without an assist on the feed.
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6, dealtTo: [3], receivedFrom: [3]),
            Kill(604, killer: 7, victim: 3),
            Kill(608, killer: 2, victim: 7));

        var d = D(Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights));

        Assert.Equal("2v1", d.GetProperty("numbers").GetString());
        Assert.Contains("Jinx", d.GetProperty("ally_champions").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void ParticipantDeadAtEntry_IsNotCounted()
    {
        // Nocturne (level 10, 32.5 s timer) died 10 s before the player's entry kill;
        // he is on the feed inside the entry window but still dead when Ahri commits.
        var timeline = Timeline(
            LevelUp(500, pid: 7, level: 10),
            Kill(590, killer: 2, victim: 7),
            Kill(600, killer: 1, victim: 6, assists: [2]),
            Kill(608, killer: 8, victim: 2));

        var d = D(Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights));

        Assert.Equal("2v1", d.GetProperty("numbers").GetString());
        Assert.Equal("2v3", d.GetProperty("became").GetString());
        var nocturne = d.GetProperty("participants").EnumerateArray()
            .Single(p => p.GetProperty("champion").GetString() == "Nocturne");
        Assert.False(nocturne.GetProperty("entry").GetBoolean());
        Assert.True(nocturne.GetProperty("credited").GetBoolean());
    }

    [Fact]
    public void ParticipantWhoRespawnedBeforeEntry_IsCounted()
    {
        // Same shape at level 1 (10 s timer): the 590 s death is over by 600 s.
        var timeline = Timeline(
            Kill(590, killer: 2, victim: 7),
            Kill(600, killer: 1, victim: 6, assists: [2]),
            Kill(608, killer: 8, victim: 2));

        var d = D(Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights));

        Assert.Equal("2v2", d.GetProperty("numbers").GetString());
    }

    [Fact]
    public void LateArrivals_ShowInBecame_NotInTheHeadline()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(610, killer: 7, victim: 1, assists: [8]),
            Kill(613, killer: 2, victim: 7, assists: [3]));

        var d = D(Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights));

        Assert.Equal("1v1", d.GetProperty("numbers").GetString());
        Assert.Equal("even", d.GetProperty("verdict").GetString());
        Assert.Equal("3v3", d.GetProperty("became").GetString());
        Assert.Equal(new[] { "EVEN_TEAMFIGHT", "TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(Assert.Single(
            TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights)));
    }

    [Fact]
    public void AwayFight_UsesWholeFightNumbers_AndTiesToAbsentOnly()
    {
        var timeline = Timeline(
            Kill(600, killer: 2, victim: 6, assists: [3]),
            Kill(605, killer: 7, victim: 3),
            Kill(610, killer: 2, victim: 7));

        var fight = Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights);

        var d = D(fight);
        Assert.Equal("away", d.GetProperty("self").GetString());
        Assert.Equal("2v2", d.GetProperty("numbers").GetString());
        Assert.False(d.TryGetProperty("entry_s", out _));
        Assert.Equal(new[] { "ABSENT_TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(fight));
    }

    // ── clocks ──────────────────────────────────────────────────────────────

    [Fact]
    public void ClockOffset_FromMatchedOwnEvents_TranslatesTheWindow()
    {
        // The live feed runs 3 s behind the server clock on both of the player's events.
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(605, killer: 2, victim: 7, assists: [1]),
            Kill(610, killer: 8, victim: 1));
        var stored = new[] { Ev("KILL", 597, id: 1), Ev("ASSIST", 602, id: 2), Ev("DEATH", 607, id: 3) };

        var fight = Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, stored).Fights);

        Assert.Equal(597, fight.GameTimeS);
        var d = D(fight);
        Assert.Equal(3, d.GetProperty("clock_offset_s").GetInt32());
        Assert.Equal(3, d.GetProperty("clock_offset_n").GetInt32());
        Assert.Equal(607, d.GetProperty("end_s").GetInt32());
        Assert.Equal(597, d.GetProperty("entry_s").GetInt32());
    }

    [Fact]
    public void ClockOffset_IsZero_WhenPairsDisagreeOrTooFew()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(605, killer: 2, victim: 7, assists: [1]),
            Kill(610, killer: 8, victim: 1));

        var disagree = new[] { Ev("KILL", 597), Ev("DEATH", 620) }; // 3 vs -10
        var one = new[] { Ev("KILL", 597) };

        Assert.Equal(600, Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, disagree).Fights).GameTimeS);
        Assert.Equal(600, Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, one).Fights).GameTimeS);
    }

    // ── gates and re-runs ───────────────────────────────────────────────────

    [Fact]
    public void NonClassicRift_DerivesNothing()
    {
        var timeline = Timeline(
            Kill(600, killer: 1, victim: 6),
            Kill(605, killer: 2, victim: 7),
            Kill(610, killer: 8, victim: 1));

        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(mapId: 12, mode: "ARAM"), timeline, SelfPuuid, []).Fights);
        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(mapId: 11, mode: "URF"), timeline, SelfPuuid, []).Fights);
        Assert.Single(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []).Fights);
    }

    [Fact]
    public void PlayerNotInMatch_OrNoFrames_DerivesNothing()
    {
        var timeline = Timeline(Kill(600, killer: 1, victim: 6), Kill(605, killer: 2, victim: 7), Kill(610, killer: 8, victim: 1));
        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(), timeline, "someone-else", []).Fights);
        Assert.Empty(TeamfightAnalyzer.Analyze(MatchPayload(), Parse("{ \"info\": {} }"), SelfPuuid, []).Fights);
    }

    [Fact]
    public void ReRun_ClearsStaleFightStamps()
    {
        var death = Ev("DEATH", 600, "{\"killer\":\"Zed\",\"fight_numbers\":\"2v4\",\"fight_verdict\":\"down\",\"fight_start_s\":590}", id: 9);

        var result = TeamfightAnalyzer.Analyze(MatchPayload(), Timeline(), SelfPuuid, [death]);

        Assert.Same(death, Assert.Single(result.StampedDeaths));
        Assert.Equal("{\"killer\":\"Zed\"}", death.Details);
    }

    [Fact]
    public void RespawnSeconds_FollowsTheBaseTableAndTimeIncreaseFactor()
    {
        Assert.Equal(10, TeamfightRules.RespawnSeconds(1, 5 * 60 * 1000), 3);
        Assert.Equal(32.5, TeamfightRules.RespawnSeconds(10, 14 * 60 * 1000), 3);
        // 40:00 → 60 steps of 0.425 % plus 40 steps of 0.30 % = +37.5 %.
        Assert.Equal(52.5 * 1.375, TeamfightRules.RespawnSeconds(18, 40 * 60 * 1000), 3);
        // Frozen at 55:00: +25.5 % +18 % +58 % = +101.5 %.
        Assert.Equal(52.5 * 2.015, TeamfightRules.RespawnSeconds(18, 70 * 60 * 1000), 3);
    }
}
