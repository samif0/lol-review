using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Stored TEAMFIGHT rows through the rest of Core: the pattern materializer anchors
/// one moment per fight under every tracked fight token and forgets stale ones, the
/// map-state pass carries both legs (fights + jungle) on one DEATH row, and a
/// capture-time re-save re-queues the game for derivation.
/// </summary>
public sealed class TeamfightStoredRowsTests
{
    private const string SelfPuuid = "self-puuid";

    private static GameEvent Ev(long gameId, string type, int t, string details = "{}") =>
        new() { GameId = gameId, EventType = type, GameTimeS = t, Details = details };

    private static GameEvent Fight(long gameId, int startS, int endS, string self = "in", string verdict = "down", string numbers = "2v3") =>
        Ev(gameId, "TEAMFIGHT", startS,
            $$"""{ "detected": true, "start_s": {{startS}}, "end_s": {{endS}}, "self": "{{self}}", "numbers": "{{numbers}}", "verdict": "{{verdict}}" }""");

    private static PatternEvidenceMaterializer Materializer(TestDatabaseScope scope) => new(
        scope.GameEvents, scope.Evidence, scope.Objectives, scope.Games,
        NullLogger<PatternEvidenceMaterializer>.Instance);

    private static async Task<long> SeedGameAsync(TestDatabaseScope scope, long gameId)
    {
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600));
        return gameId;
    }

    // ── materializer ────────────────────────────────────────────────────────

    [Fact]
    public async Task Materializer_StoredOwnFight_AnchorsOncePerTrackedFightToken_OverTheSpan()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedGameAsync(scope, 7601);
        var objA = await scope.Objectives.CreateAsync("Win fights", "teamfight");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objA, new[] { "TEAMFIGHT" });
        var objB = await scope.Objectives.CreateAsync("Stop taking bad fights", "teamfight");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objB, new[] { "OUTNUMBERED_TEAMFIGHT" });

        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            Ev(gameId, "KILL", 600), Ev(gameId, "DEATH", 606), Ev(gameId, "ASSIST", 612),
        });
        await scope.GameEvents.AppendEventsAsync(gameId, new[] { Fight(gameId, 595, 614) });

        await Materializer(scope).MaterializeForGameAsync(gameId);

        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(2, rows.Count);
        var generic = Assert.Single(rows, r => r.SourceKey == "objev:TEAMFIGHT:600");
        var down = Assert.Single(rows, r => r.SourceKey == "objev:OUTNUMBERED_TEAMFIGHT:600");
        Assert.Equal("Teamfight", generic.Title);
        Assert.Equal("Outnumbered Fight", down.Title);
        Assert.Equal(EvidencePolarities.Bad, down.Polarity);
        Assert.Equal(EvidencePolarities.Neutral, generic.Polarity);
        Assert.Equal(600 - PatternConstants.TeamfightLeadSeconds, generic.StartTimeSeconds);
        Assert.Equal(614 + PatternConstants.TeamfightTrailSeconds, generic.EndTimeSeconds);
    }

    [Fact]
    public async Task Materializer_AwayFight_NeverAnchors()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedGameAsync(scope, 7602);
        var obj = await scope.Objectives.CreateAsync("Fights I skipped", "teamfight");
        await scope.Objectives.SetEventTokensForObjectiveAsync(obj, new[] { "ABSENT_TEAMFIGHT", "TEAMFIGHT" });
        await scope.GameEvents.AppendEventsAsync(gameId, new[] { Fight(gameId, 600, 612, self: "away", numbers: "3v3", verdict: "even") });

        await Materializer(scope).MaterializeForGameAsync(gameId);

        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
    }

    [Fact]
    public async Task Materializer_ReplacesAStaleSyntheticAnchor_WhenTheStoredFightMovesIt_ButKeepsNotedRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedGameAsync(scope, 7603);
        var obj = await scope.Objectives.CreateAsync("Win fights", "teamfight");
        await scope.Objectives.SetEventTokensForObjectiveAsync(obj, new[] { "TEAMFIGHT" });
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            Ev(gameId, "KILL", 600), Ev(gameId, "DEATH", 606), Ev(gameId, "ASSIST", 612),
            Ev(gameId, "KILL", 900), Ev(gameId, "DEATH", 905), Ev(gameId, "ASSIST", 910),
        });
        var materializer = Materializer(scope);

        // Game end: synthetic anchors at 600 and 900. The user notes the second one.
        await materializer.MaterializeForGameAsync(gameId);
        var before = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(2, before.Count);
        var noted = before.Single(r => r.SourceKey == "objev:TEAMFIGHT:900");
        await scope.Evidence.UpdateNoteAsync(noted.Id, "kept my note");

        // The post-game pass lands: the first fight has no capture events near it any
        // more (the row anchors at 580), the second fight is gone from the timeline.
        await scope.GameEvents.SaveEventsAsync(gameId, new[] { Ev(gameId, "KILL", 900), Ev(gameId, "DEATH", 905), Ev(gameId, "ASSIST", 910) });
        await scope.GameEvents.AppendEventsAsync(gameId, new[] { Fight(gameId, 580, 590) });
        await materializer.MaterializeForGameAsync(gameId);

        var after = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Contains(after, r => r.SourceKey == "objev:TEAMFIGHT:580");   // the stored fight
        Assert.DoesNotContain(after, r => r.SourceKey == "objev:TEAMFIGHT:600"); // stale synthetic, un-noted → gone
        Assert.Contains(after, r => r.SourceKey == "objev:TEAMFIGHT:900");   // synthetic safety net still resolves it
        Assert.Equal("kept my note", after.Single(r => r.SourceKey == "objev:TEAMFIGHT:900").Note);
    }

    [Fact]
    public async Task Materializer_KeepsFightAnchors_WhenTheTokenIsNoLongerTracked_AndKeepsTriagedRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedGameAsync(scope, 7606);
        var obj = await scope.Objectives.CreateAsync("Win fights", "teamfight");
        await scope.Objectives.SetEventTokensForObjectiveAsync(obj, new[] { "TEAMFIGHT" });
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            Ev(gameId, "KILL", 600), Ev(gameId, "DEATH", 606), Ev(gameId, "ASSIST", 612),
            Ev(gameId, "KILL", 900), Ev(gameId, "DEATH", 905), Ev(gameId, "ASSIST", 910),
        });
        var materializer = Materializer(scope);
        await materializer.MaterializeForGameAsync(gameId);
        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(2, rows.Count);
        var dismissed = rows.Single(r => r.SourceKey == "objev:TEAMFIGHT:900");
        await scope.Evidence.UpdateStatusAsync(dismissed.Id, EvidenceStatuses.Dismissed);

        // The objective stops tracking fights: history stays, nothing new is written.
        await scope.Objectives.SetEventTokensForObjectiveAsync(obj, new[] { "DEATH" });
        await materializer.MaterializeForGameAsync(gameId);
        var keys = (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Select(r => r.SourceKey).ToList();
        Assert.Contains("objev:TEAMFIGHT:600", keys);
        Assert.Contains("objev:TEAMFIGHT:900", keys);

        // The second fight vanishes from the event stream: its dismissed anchor is the
        // user's triage and survives; an untouched stale anchor would not.
        await scope.GameEvents.SaveEventsAsync(gameId, new[] { Ev(gameId, "KILL", 600), Ev(gameId, "DEATH", 606), Ev(gameId, "ASSIST", 612) });
        await materializer.MaterializeForGameAsync(gameId);
        keys = (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Select(r => r.SourceKey).ToList();
        Assert.Contains("objev:TEAMFIGHT:900", keys);
        Assert.Contains("objev:TEAMFIGHT:600", keys);
    }

    // ── MapStateAnalyzer: both legs on one row ──────────────────────────────

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Match(bool withJungler) => Parse($$"""
        { "info": { "mapId": 11, "gameMode": "CLASSIC", "participants": [
            { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "MIDDLE", "championName": "Ahri" },
            { "puuid": "p2", "participantId": 2, "teamId": 100, "teamPosition": "{{(withJungler ? "JUNGLE" : "")}}", "championName": "LeeSin" },
            { "puuid": "p6", "participantId": 6, "teamId": 200, "teamPosition": "MIDDLE", "championName": "Zed" },
            { "puuid": "p7", "participantId": 7, "teamId": 200, "teamPosition": "{{(withJungler ? "JUNGLE" : "")}}", "championName": "Nocturne" }
        ] } }
        """);

    // A 5:00 fight next to the player: Zed kills Ahri with Nocturne assisting, then the
    // trade back. Frames at 4:00 and 6:00 park the enemy jungler far away.
    private static JsonElement TimelineWithFight() => Parse("""
        { "info": { "frames": [
            { "timestamp": 240000, "participantFrames": {
                "1": { "position": { "x": 7000, "y": 7000 } }, "2": { "position": { "x": 1000, "y": 1000 } },
                "7": { "position": { "x": 13000, "y": 13000 } } }, "events": [] },
            { "timestamp": 360000, "participantFrames": {
                "1": { "position": { "x": 7000, "y": 7000 } }, "2": { "position": { "x": 1000, "y": 1000 } },
                "7": { "position": { "x": 13000, "y": 13000 } } },
              "events": [
                { "type": "CHAMPION_KILL", "timestamp": 300000, "killerId": 6, "victimId": 1, "assistingParticipantIds": [7], "position": { "x": 7000, "y": 5000 } },
                { "type": "CHAMPION_KILL", "timestamp": 305000, "killerId": 2, "victimId": 6, "assistingParticipantIds": [], "position": { "x": 7100, "y": 5100 } },
                { "type": "CHAMPION_KILL", "timestamp": 309000, "killerId": 2, "victimId": 7, "assistingParticipantIds": [], "position": { "x": 7050, "y": 5200 } }
              ] }
        ] } }
        """);

    [Fact]
    public void MapStateAnalyzer_NoJungler_StillDerivesFights_AndProximityStaysEmpty()
    {
        var death = new GameEvent { Id = 1, EventType = "DEATH", GameTimeS = 300, Details = "{\"killer\":\"Zed\"}" };

        var result = MapStateAnalyzer.Analyze(Match(withJungler: false), TimelineWithFight(), SelfPuuid, [death]);

        Assert.Empty(result.ProximityEvents);
        var fight = Assert.Single(result.TeamfightEvents);
        Assert.Equal("1v2", Parse(fight.Details).GetProperty("numbers").GetString());
        var stamped = Assert.Single(result.StampedDeaths);
        Assert.Same(death, stamped);
        var d = Parse(death.Details);
        Assert.Equal("1v2", d.GetProperty("fight_numbers").GetString());
        Assert.False(d.TryGetProperty("map_state", out _));
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void MapStateAnalyzer_BothLegs_StampOneDeathOnce_WithBothKeyFamilies()
    {
        var death = new GameEvent { Id = 1, EventType = "DEATH", GameTimeS = 300, Details = "{\"killer\":\"Zed\"}" };

        var result = MapStateAnalyzer.Analyze(Match(withJungler: true), TimelineWithFight(), SelfPuuid, [death]);

        Assert.Single(result.TeamfightEvents);
        Assert.Same(death, Assert.Single(result.StampedDeaths));
        var d = Parse(death.Details);
        Assert.Equal("Zed", d.GetProperty("killer").GetString());
        Assert.Equal("1v2", d.GetProperty("fight_numbers").GetString());
        Assert.Equal("down", d.GetProperty("fight_verdict").GetString());
        Assert.True(d.GetProperty("map_state").GetBoolean());
        Assert.True(d.GetProperty("fog_death").GetBoolean()); // Nocturne dark 300 s and on the kill
        Assert.Equal(3, MapStateAnalyzer.Version);
        Assert.Empty(MapStateAnalysis.Empty.TeamfightEvents);
        Assert.True(MapStateAnalysis.Empty.IsEmpty);
    }

    [Fact]
    public void MapStateAnalyzer_TeamfightLegFault_DoesNotBlockTheJungleLeg()
    {
        // A fractional participantId in the damage array is read only by the teamfight
        // leg (JsonElement.GetInt32 throws on 2.5); the jungle leg never touches it. The
        // fault must be contained: proximity still derives, fights come back empty.
        var timeline = Parse("""
            { "info": { "frames": [
                { "timestamp": 300000, "participantFrames": {
                    "1": { "position": { "x": 7000, "y": 7000 } }, "7": { "position": { "x": 9000, "y": 9000 } } },
                  "events": [ { "type": "CHAMPION_KILL", "timestamp": 290000, "killerId": 6, "victimId": 2,
                                "assistingParticipantIds": [], "victimDamageReceived": [ { "participantId": 2.5 } ],
                                "position": { "x": 3000, "y": 3000 } } ] }
            ] } }
            """);

        Assert.ThrowsAny<Exception>(() => TeamfightAnalyzer.Analyze(Match(withJungler: true), timeline, SelfPuuid, []));

        var result = MapStateAnalyzer.Analyze(Match(withJungler: true), timeline, SelfPuuid, []);

        Assert.Single(result.ProximityEvents);
        Assert.Empty(result.TeamfightEvents);
    }

    // ── repository: re-capture re-queues the pass ──────────────────────────

    [Fact]
    public async Task ClearMapStateVersion_PutsTheGameBackInTheMissingSet()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedGameAsync(scope, 7604);
        await scope.Games.UpdateMapStateVersionAsync(gameId, MapStateAnalyzer.Version);
        Assert.DoesNotContain(gameId, await scope.Games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version));

        await scope.Games.ClearMapStateVersionAsync(gameId);

        Assert.Contains(gameId, await scope.Games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version));
    }

    [Fact]
    public async Task StoredFightRows_RoundTripThroughTheRepository_AndDeleteByType()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedGameAsync(scope, 7605);
        await scope.GameEvents.SaveEventsAsync(gameId, new[] { Ev(gameId, "KILL", 600) });
        await scope.GameEvents.AppendEventsAsync(gameId, new[] { Fight(gameId, 595, 614), Fight(gameId, 900, 910, self: "away") });

        var events = await scope.GameEvents.GetEventsAsync(gameId);
        Assert.Equal(2, events.Count(TeamfightClustering.IsStoredTeamfight));
        var spans = TeamfightClustering.Resolve(events);
        Assert.Equal(2, spans.Count);
        Assert.Equal(600, spans[0].StartS); // anchored on the KILL member
        Assert.Equal(595, spans[0].FightStartS);

        await scope.GameEvents.DeleteEventsByTypeAsync(gameId, "TEAMFIGHT");
        Assert.Single(await scope.GameEvents.GetEventsAsync(gameId));
    }
}
