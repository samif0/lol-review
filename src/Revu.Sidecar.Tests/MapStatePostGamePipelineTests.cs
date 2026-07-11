using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// End-to-end contract for the POST-GAME map-state flow the game-flow coordinator
/// fires after every saved game: <see cref="MapStateBackfillService.RunAsync"/> with
/// a fetched match + timeline must leave the game's event stream carrying the
/// jungle-proximity visit rows and death stamps that the VOD timeline renders —
/// and the read-time tokens objectives tie to must fire on those exact rows.
/// Real repositories against a temp DB; only the Riot HTTP hop is stubbed.
/// </summary>
public sealed class MapStatePostGamePipelineTests
{
    private const string SelfPuuid = "self-puuid";
    private const long GameId = 5_598_958_689; // shape of a real NA game id

    private sealed class StubMatchClient : IRiotMatchClient
    {
        public JsonElement? Match;
        public JsonElement? Timeline;
        public int MatchCalls;
        public int TimelineCalls;

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            MatchCalls++;
            return Task.FromResult(Match);
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default)
        {
            TimelineCalls++;
            return Task.FromResult(Timeline);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // Self mid (p1, team 100), ally jungler (p2, team 100), enemy jungler (p7, team 200).
    private static JsonElement MatchPayload() => Parse($$"""
        { "info": { "participants": [
            { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "MIDDLE", "championName": "Qiyana" },
            { "puuid": "ally-jg", "participantId": 2, "teamId": 100, "teamPosition": "JUNGLE", "championName": "LeeSin" },
            { "puuid": "enemy-jg", "participantId": 7, "teamId": 200, "teamPosition": "JUNGLE", "championName": "Nocturne" }
        ] } }
        """);

    // The reported failure shape: enemy jungler parked far at both frames, collapses
    // mid-minute and kills the player at 5:00 — the only positional trace is the kill.
    private static JsonElement TimelinePayload() => Parse($$"""
        { "info": { "frames": [
            { "timestamp": 240000,
              "participantFrames": {
                "1": { "position": { "x": 7000, "y": 7000 } },
                "2": { "position": { "x": 1000, "y": 1000 } },
                "7": { "position": { "x": 13000, "y": 13000 } } },
              "events": [] },
            { "timestamp": 360000,
              "participantFrames": {
                "1": { "position": { "x": 7000, "y": 7000 } },
                "2": { "position": { "x": 1000, "y": 1000 } },
                "7": { "position": { "x": 13000, "y": 13000 } } },
              "events": [
                { "type": "CHAMPION_KILL", "timestamp": 300000, "killerId": 7,
                  "victimId": 1, "assistingParticipantIds": [],
                  "position": { "x": 7000, "y": 5000 } }
              ] }
        ] } }
        """);

    [Fact]
    public async Task PostGamePass_LeavesRenderableMarkersOnTheGameEventStream()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Qiyana"); // now + unreviewed → in the queue

        // Capture-time events from the live kill feed (what EOG persistence wrote).
        await events.SaveEventsAsync(GameId,
        [
            new GameEvent { EventType = "DEATH", GameTimeS = 300, Details = "{\"killer\":\"Nocturne\"}" },
            new GameEvent { EventType = "KILL", GameTimeS = 410, Details = "{\"victim\":\"Sylas\"}" },
        ]);

        var stub = new StubMatchClient { Match = MatchPayload(), Timeline = TimelinePayload() };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = SelfPuuid });
        var service = new MapStateBackfillService(
            scope.Games, events, stub, config, NullLogger<MapStateBackfillService>.Instance);

        // The exact call the game-flow coordinator fires after EOG persistence.
        var result = await service.RunAsync(maxGames: 5);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Failed);

        var stream = await events.GetEventsAsync(GameId);

        // 1. The between-frames gank shows as an enemy jungle-proximity visit.
        var prox = Assert.Single(stream, e => e.EventType == "JUNGLE_PROXIMITY");
        using (var d = JsonDocument.Parse(prox.Details))
        {
            Assert.Equal("enemy", d.RootElement.GetProperty("who").GetString());
            Assert.Equal("Nocturne", d.RootElement.GetProperty("champion").GetString());
            Assert.True(d.RootElement.GetProperty("distance").GetInt32() <= 100); // closest approach = the kill
        }
        Assert.InRange(prox.GameTimeS, 270, 300);

        // 2. The death got its map-state stamps, existing keys preserved.
        var death = Assert.Single(stream, e => e.EventType == "DEATH");
        using (var d = JsonDocument.Parse(death.Details))
        {
            Assert.Equal("Nocturne", d.RootElement.GetProperty("killer").GetString());
            Assert.True(d.RootElement.GetProperty("map_state").GetBoolean());
            Assert.True(d.RootElement.GetProperty("fog_death").GetBoolean()); // dark 300s, he was the killer
            Assert.True(d.RootElement.TryGetProperty("enemy_jg_dark_s", out _));
        }

        // 3. The read-time tokens the VOD priority lane matches on fire for BOTH rows —
        //    an objective tracking the enemy jungler or fog deaths lights these up.
        Assert.Equal("ENEMY_JUNGLE_PROXIMITY", ObjectiveEventTieResolver.EventTokens(prox)[0]);
        Assert.Contains("FOG_DEATH", ObjectiveEventTieResolver.EventTokens(death));

        // 4. Untouched capture-time rows stay untouched.
        var kill = Assert.Single(stream, e => e.EventType == "KILL");
        Assert.Equal("{\"victim\":\"Sylas\"}", kill.Details);

        // 5. Idempotency: the game is marked processed — a second pass fetches nothing.
        var again = await service.RunAsync(maxGames: 5);
        Assert.Equal(0, again.Scanned);
        Assert.Equal(1, stub.MatchCalls);
        Assert.Equal(1, stub.TimelineCalls);
    }
}
