using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pure unit tests for <see cref="MapStateAnalyzer"/> — jungle-proximity events from
/// timeline frames and death map-state stamping (distances, darkness, fog deaths).
/// Payload shapes mirror Riot's Match-V5 match + timeline documents.
/// </summary>
public sealed class MapStateAnalyzerTests
{
    private const string SelfPuuid = "self-puuid";

    // Roster: self = participant 1 (mid, team 100), ally jungler = 2 (team 100),
    // enemy jungler = 7 (team 200).
    private static JsonElement MatchPayload() => Parse($$"""
        {
          "info": {
            "participants": [
              { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "MIDDLE", "championName": "Ahri" },
              { "puuid": "ally-jg", "participantId": 2, "teamId": 100, "teamPosition": "JUNGLE", "championName": "LeeSin" },
              { "puuid": "enemy-jg", "participantId": 7, "teamId": 200, "teamPosition": "JUNGLE", "championName": "Nocturne" }
            ]
          }
        }
        """);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Timeline(params string[] frames) => Parse($$"""
        { "info": { "frames": [ {{string.Join(",", frames)}} ] } }
        """);

    private static string Frame(long tsMs, string participantFrames, string events = "") => $$"""
        { "timestamp": {{tsMs}},
          "participantFrames": { {{participantFrames}} },
          "events": [ {{events}} ] }
        """;

    private static string PFrame(int pid, int x, int y) =>
        $$"""  "{{pid}}": { "position": { "x": {{x}}, "y": {{y}} } } """;

    private static string KillEvent(long tsMs, int killerId, int victimId, int x, int y, params int[] assistIds) => $$"""
        { "type": "CHAMPION_KILL", "timestamp": {{tsMs}}, "killerId": {{killerId}},
          "victimId": {{victimId}}, "assistingParticipantIds": [{{string.Join(",", assistIds)}}],
          "position": { "x": {{x}}, "y": {{y}} } }
        """;

    private static GameEvent Death(int gameTimeS, string details = "{}") =>
        new() { Id = 1, EventType = "DEATH", GameTimeS = gameTimeS, Details = details };

    private static JsonElement Details(GameEvent e) => Parse(e.Details);

    // ── proximity events ─────────────────────────────────────────────────────

    [Fact]
    public void EmitsEnemyProximity_WithinRadius_DuringLaning()
    {
        // 5:00 frame — enemy jungler ~2828 units away (inside 4000), ally ~7810 (outside).
        var timeline = Timeline(Frame(300_000, string.Join(",",
            PFrame(1, 7000, 7000), PFrame(2, 2000, 13000), PFrame(7, 9000, 9000))));

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        var e = Assert.Single(result.ProximityEvents);
        Assert.Equal(GameEvent.EventTypes.JungleProximity, e.EventType);
        Assert.Equal(300, e.GameTimeS);
        var d = Details(e);
        Assert.Equal("enemy", d.GetProperty("who").GetString());
        Assert.Equal("Nocturne", d.GetProperty("champion").GetString());
        Assert.Equal(2828, d.GetProperty("distance").GetInt32());
        Assert.True(d.GetProperty("detected").GetBoolean());
    }

    [Fact]
    public void EmitsAllyProximity_WithWhoAlly()
    {
        var timeline = Timeline(Frame(300_000, string.Join(",",
            PFrame(1, 7000, 7000), PFrame(2, 8000, 8000), PFrame(7, 13000, 1000))));

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        var e = Assert.Single(result.ProximityEvents);
        Assert.Equal("ally", Details(e).GetProperty("who").GetString());
        Assert.Equal("LeeSin", Details(e).GetProperty("champion").GetString());
    }

    [Fact]
    public void NoProximity_BeforeTwoMinutes()
    {
        // Spawn/leash window — everyone near everyone; deliberately not emitted.
        var timeline = Timeline(Frame(60_000, string.Join(",",
            PFrame(1, 7000, 7000), PFrame(7, 7500, 7500))));

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        Assert.Empty(result.ProximityEvents);
    }

    [Fact]
    public void NoProximity_AfterLaningPhase()
    {
        // 15:00 — mid-game grouping makes proximity meaningless; not emitted.
        var timeline = Timeline(Frame(900_000, string.Join(",",
            PFrame(1, 7000, 7000), PFrame(7, 7500, 7500))));

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        Assert.Empty(result.ProximityEvents);
    }

    [Fact]
    public void NoProximity_BeyondThreatRadius()
    {
        var timeline = Timeline(Frame(300_000, string.Join(",",
            PFrame(1, 2000, 2000), PFrame(7, 12000, 12000))));

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, []);

        Assert.Empty(result.ProximityEvents);
    }

    [Fact]
    public void NothingDerived_WhenNoJunglers()
    {
        // ARAM-shaped match: no JUNGLE teamPosition anywhere → don't guess.
        var match = Parse($$"""
            { "info": { "participants": [
                { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "", "championName": "Ahri" }
            ] } }
            """);
        var timeline = Timeline(Frame(300_000, string.Join(",",
            PFrame(1, 7000, 7000), PFrame(7, 7100, 7100))));
        var death = Death(300);

        var result = MapStateAnalyzer.Analyze(match, timeline, SelfPuuid, [death]);

        Assert.Empty(result.ProximityEvents);
        Assert.Empty(result.StampedDeaths);
        Assert.Equal("{}", death.Details);
    }

    [Fact]
    public void NothingDerived_WhenPlayerNotInMatch()
    {
        var timeline = Timeline(Frame(300_000, PFrame(7, 7000, 7000)));

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, "someone-else", []);

        Assert.Empty(result.ProximityEvents);
    }

    // ── death stamping ───────────────────────────────────────────────────────

    [Fact]
    public void StampsDeath_WithInterpolatedJunglerDistances()
    {
        // Enemy jungler walks (6000,6000)→(8000,8000) across the 4:00 and 6:00 frames;
        // at the 5:00 death he interpolates to (7000,7000) — 2000 units from the death
        // spot (7000,5000). Killer 8 is a laner, so no fog/gank attribution here.
        var timeline = Timeline(
            Frame(240_000, string.Join(",",
                PFrame(1, 7000, 7000), PFrame(2, 7000, 11000), PFrame(7, 6000, 6000))),
            Frame(360_000, string.Join(",",
                PFrame(1, 7000, 7000), PFrame(2, 7000, 11000), PFrame(7, 8000, 8000)),
                KillEvent(300_000, killerId: 8, victimId: 1, x: 7000, y: 5000)));
        var death = Death(302); // live-feed clock, 2s skew from the timeline kill

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        Assert.Same(death, Assert.Single(result.StampedDeaths));
        var d = Details(death);
        Assert.True(d.GetProperty("map_state").GetBoolean());
        Assert.Equal(2000, d.GetProperty("enemy_jg_dist").GetInt32());
        Assert.Equal(6000, d.GetProperty("ally_jg_dist").GetInt32());
        // Never revealed before the death → dark since 0:00.
        Assert.Equal(300, d.GetProperty("enemy_jg_dark_s").GetInt32());
        Assert.False(d.TryGetProperty("fog_death", out _));
    }

    [Fact]
    public void FogDeath_WhenUnseenEnemyJunglerIsOnTheKill()
    {
        var timeline = Timeline(
            Frame(240_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 6000, 6000))),
            Frame(360_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 8000, 8000)),
                KillEvent(300_000, killerId: 7, victimId: 1, x: 7000, y: 5000)));
        var death = Death(300);

        MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        var d = Details(death);
        Assert.Equal(300, d.GetProperty("enemy_jg_dark_s").GetInt32());
        Assert.True(d.GetProperty("fog_death").GetBoolean());
    }

    [Fact]
    public void NoFogDeath_WhenEnemyJunglerRecentlyRevealed()
    {
        // The enemy jungler killed someone at 4:30 (map-visible reveal), then kills
        // the player at 5:00 — only 30s dark, below the fog threshold.
        var timeline = Timeline(
            Frame(240_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 6000, 6000))),
            Frame(360_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 8000, 8000)),
                KillEvent(270_000, killerId: 7, victimId: 9, x: 10000, y: 10000) + "," +
                KillEvent(300_000, killerId: 7, victimId: 1, x: 7000, y: 5000)));
        var death = Death(300);

        MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        var d = Details(death);
        Assert.Equal(30, d.GetProperty("enemy_jg_dark_s").GetInt32());
        Assert.False(d.TryGetProperty("fog_death", out _));
    }

    [Fact]
    public void FogDeath_CountsAssisterAsOnTheKill()
    {
        var timeline = Timeline(
            Frame(240_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 6000, 6000))),
            Frame(360_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 8000, 8000)),
                KillEvent(300_000, killerId: 8, victimId: 1, x: 7000, y: 5000, assistIds: 7)));
        var death = Death(300);

        MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        Assert.True(Details(death).GetProperty("fog_death").GetBoolean());
    }

    [Fact]
    public void DeathNotStamped_WhenNoTimelineDeathWithinTolerance()
    {
        var timeline = Timeline(
            Frame(360_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 8000, 8000)),
                KillEvent(300_000, killerId: 7, victimId: 1, x: 7000, y: 5000)));
        var death = Death(500); // 200s from the only timeline death

        var result = MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        Assert.Empty(result.StampedDeaths);
        Assert.Equal("{}", death.Details);
    }

    [Fact]
    public void Stamping_PreservesExistingDetailsKeys()
    {
        var timeline = Timeline(
            Frame(360_000, string.Join(",", PFrame(1, 7000, 7000), PFrame(7, 8000, 8000)),
                KillEvent(300_000, killerId: 7, victimId: 1, x: 7000, y: 5000)));
        var death = Death(300, "{\"killer\":\"Nocturne\",\"jungle_gank\":true}");

        MapStateAnalyzer.Analyze(MatchPayload(), timeline, SelfPuuid, [death]);

        var d = Details(death);
        Assert.Equal("Nocturne", d.GetProperty("killer").GetString());
        Assert.True(d.GetProperty("jungle_gank").GetBoolean());
        Assert.True(d.GetProperty("map_state").GetBoolean());
    }

    // ── persistence round-trip ───────────────────────────────────────────────

    [Fact]
    public async Task Repository_RoundTripsMapStateColumnsAndDerivedRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var saved = await scope.Games.SaveAsync(new GameStats
        {
            GameId = 930_001,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            QueueType = "Ranked Solo/Duo",
            ChampionName = "Ahri",
            Win = true,
            GameDuration = 1900,
        });
        Assert.True(saved >= 0);

        var missing = await scope.Games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version);
        Assert.Contains(930_001, missing);

        // Capture-time events, then the backfill's append — append must not clear.
        await scope.GameEvents.SaveEventsAsync(930_001,
            [new GameEvent { EventType = "DEATH", GameTimeS = 300, Details = "{}" }]);
        await scope.GameEvents.AppendEventsAsync(930_001,
        [
            new GameEvent { EventType = "JUNGLE_PROXIMITY", GameTimeS = 240, Details = "{\"who\":\"enemy\"}" },
            new GameEvent { EventType = "JUNGLE_PROXIMITY", GameTimeS = 300, Details = "{\"who\":\"ally\"}" },
        ]);
        var events = await scope.GameEvents.GetEventsAsync(930_001);
        Assert.Equal(3, events.Count);

        // In-place Details rewrite persists (the death-stamp path).
        var death = events.Single(e => e.EventType == "DEATH");
        await scope.GameEvents.UpdateEventDetailsAsync(death.Id, "{\"map_state\":true,\"enemy_jg_dist\":2000}");
        events = await scope.GameEvents.GetEventsAsync(930_001);
        Assert.Contains("enemy_jg_dist", events.Single(e => e.EventType == "DEATH").Details);

        // Type-scoped delete removes only the derived rows (the re-run path).
        await scope.GameEvents.DeleteEventsByTypeAsync(930_001, "JUNGLE_PROXIMITY");
        events = await scope.GameEvents.GetEventsAsync(930_001);
        Assert.Single(events);
        Assert.Equal("DEATH", events[0].EventType);

        // Marking processed drains the missing set; a version bump re-queues.
        await scope.Games.UpdateMapStateVersionAsync(930_001, MapStateAnalyzer.Version);
        missing = await scope.Games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version);
        Assert.DoesNotContain(930_001, missing);
        missing = await scope.Games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version + 1);
        Assert.Contains(930_001, missing);
    }
}
