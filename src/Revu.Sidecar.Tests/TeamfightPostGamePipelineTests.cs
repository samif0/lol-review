using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// End-to-end contract for the v3.8 TEAMFIGHT NUMBERS flow: the post-game map-state
/// pass (<see cref="MapStateBackfillService.RunAsync"/>) writes one TEAMFIGHT row per
/// fight from the Match-V5 timeline, and <see cref="VodSnapshotBuilder"/> renders that
/// row as exactly one pin carrying the numbers. Also pins down the ABSENT contract (a
/// fight without the player is a dim pin: never tied to TEAMFIGHT trackers, never
/// clipped) and the pattern-anchor re-materialization. Real repositories against a
/// temp DB; only the Riot HTTP hop is stubbed.
/// </summary>
public sealed class TeamfightPostGamePipelineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetedRecovery_PreservesSnapshotAndLiveRows_AndRerunsWithoutDuplicates(bool hasSnapshot)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await events.SaveEventsAsync(GameId, [new GameEvent { EventType = "KILL", GameTimeS = 600 }]);
        var originalId = Assert.Single(await events.GetEventsAsync(GameId)).Id;
        var originalObjective = await TrackAsync(scope, "Original trade objective", "TRADE");
        if (hasSnapshot)
        {
            var live = await Revu.Core.Services.EventProcessing.LiveProcessingCatalog.Create(
                [new(originalObjective,"TRADE")]).CompleteAsync();
            await events.SaveProcessingReportAsync(GameId, live);
        }
        var laterObjective = await TrackAsync(scope, "Next match objective", "TRADE");
        var match = System.Text.Json.Nodes.JsonNode.Parse(MatchPayload().GetRawText())!;
        match["metadata"] = new System.Text.Json.Nodes.JsonObject { ["matchId"] = "TEST_1" };
        var roster = match["info"]!["participants"]!.AsArray();
        foreach (var id in new[] { 4,5,9,10 }) roster.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["participantId"] = id, ["puuid"] = $"p{id}", ["teamId"] = id <= 5 ? 100 : 200,
            ["championName"] = $"Champion{id}"
        });
        var timeline = System.Text.Json.Nodes.JsonNode.Parse(Timeline(Kill(600,1,6)).GetRawText())!;
        timeline["metadata"] = new System.Text.Json.Nodes.JsonObject { ["matchId"] = "TEST_1" };
        var death = timeline["info"]!["frames"]![1]!["events"]![0]!;
        death["victimDamageDealt"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"participantId":1,"name":"Zed","type":"OTHER","magicDamage":20,"physicalDamage":0,"trueDamage":0}]""");
        death["victimDamageReceived"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"participantId":1,"name":"Ahri","type":"OTHER","magicDamage":40,"physicalDamage":0,"trueDamage":0}]""");
        var client = new StubMatchClient { Match = Parse(match.ToJsonString()), Timeline = Parse(timeline.ToJsonString()) };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = SelfPuuid });
        var service = new MapStateBackfillService(scope.Games, events, client, config, NullLogger<MapStateBackfillService>.Instance);
        for (int run = 0; run < 2; run++)
        {
            Assert.Equal(0, (await service.RunForGameAsync(GameId)).Failed);
            var report = await events.GetProcessingReportAsync(GameId);
            if (!hasSnapshot) Assert.Null(report);
            else
            {
                Assert.Equal("unavailable", Assert.Single(report!.Coverage).Status);
                var candidate = Assert.Single(report.Recovery!.Events);
                Assert.True(Revu.Core.Services.EventProcessing.EventEligibility.WasSubscribed(candidate, originalObjective));
                Assert.False(Revu.Core.Services.EventProcessing.EventEligibility.WasSubscribed(candidate, laterObjective));
                Assert.False(Revu.Core.Services.EventProcessing.EventEligibility.IsEligible(candidate));
            }
            var rows = await events.GetEventsAsync(GameId);
            Assert.Equal(originalId, Assert.Single(rows, e=>e.EventType == "KILL").Id);
            Assert.DoesNotContain(rows, e=>e.EventType == "RECORDED_EXCHANGE");
        }
    }

    private const string SelfPuuid = "self-puuid";
    private const long GameId = 5_598_958_690;

    private sealed class StubMatchClient : IRiotMatchClient
    {
        public JsonElement? Match;
        public JsonElement? Timeline;

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult(Match);

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult(Timeline);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // Self = 1 (mid, team 100) with allies 2 (jungle) and 3 (bot); enemies 6, 7, 8.
    private static JsonElement MatchPayload() => Parse($$"""
        { "info": { "mapId": 11, "gameMode": "CLASSIC", "participants": [
            { "puuid": "{{SelfPuuid}}", "participantId": 1, "teamId": 100, "teamPosition": "MIDDLE", "championName": "Ahri" },
            { "puuid": "p2", "participantId": 2, "teamId": 100, "teamPosition": "JUNGLE", "championName": "LeeSin" },
            { "puuid": "p3", "participantId": 3, "teamId": 100, "teamPosition": "BOTTOM", "championName": "Jinx" },
            { "puuid": "p6", "participantId": 6, "teamId": 200, "teamPosition": "MIDDLE", "championName": "Zed" },
            { "puuid": "p7", "participantId": 7, "teamId": 200, "teamPosition": "JUNGLE", "championName": "Nocturne" },
            { "puuid": "p8", "participantId": 8, "teamId": 200, "teamPosition": "TOP", "championName": "Garen" }
        ] } }
        """);

    // Two frames (0 and 15:00) with the junglers parked far from the player, so the
    // jungle leg derives nothing and the kills are the only signal.
    private static JsonElement Timeline(params string[] events) => Parse($$"""
        { "info": { "frames": [
            { "timestamp": 0,
              "participantFrames": {
                "1": { "position": { "x": 7000, "y": 7000 } },
                "2": { "position": { "x": 1000, "y": 1000 } },
                "7": { "position": { "x": 13000, "y": 13000 } } },
              "events": [] },
            { "timestamp": 900000,
              "participantFrames": {
                "1": { "position": { "x": 7000, "y": 7000 } },
                "2": { "position": { "x": 1000, "y": 1000 } },
                "7": { "position": { "x": 13000, "y": 13000 } } },
              "events": [ {{string.Join(",", events)}} ] }
        ] } }
        """);

    private static string Kill(int s, int killer, int victim, int[]? assists = null) => $$"""
        { "type": "CHAMPION_KILL", "timestamp": {{s * 1000}}, "killerId": {{killer}}, "victimId": {{victim}},
          "assistingParticipantIds": [{{string.Join(",", assists ?? [])}}],
          "position": { "x": 7000, "y": 7000 } }
        """;

    // Three kills within 14 s at one spot, the player credited on the first and last:
    // 2v1 (up) when the player committed, 3v2 over the whole fight, 2 kills for / 1 against.
    private static JsonElement OwnFightTimeline() => Timeline(
        Kill(600, killer: 1, victim: 6, assists: [2]),
        Kill(605, killer: 7, victim: 3),
        Kill(610, killer: 2, victim: 7, assists: [1]));

    // The same shape without the player on any kill feed line.
    private static JsonElement AwayFightTimeline() => Timeline(
        Kill(600, killer: 2, victim: 6, assists: [3]),
        Kill(605, killer: 7, victim: 3),
        Kill(610, killer: 2, victim: 7));

    // A 1v3 (down) fight at 10:00 and a 2v1 (up) fight at 15:00, both with the player.
    private static JsonElement DownThenUpTimeline() => Timeline(
        Kill(600, killer: 6, victim: 1, assists: [7, 8]),
        Kill(604, killer: 2, victim: 6),
        Kill(608, killer: 8, victim: 2),
        Kill(900, killer: 1, victim: 6, assists: [2]),
        Kill(905, killer: 2, victim: 7, assists: [1]),
        Kill(910, killer: 8, victim: 3));

    private static JsonElement D(GameEvent e) => Parse(e.Details);

    private static MapStateBackfillService Service(
        SidecarWriteScope scope, GameEventsRepository events, JsonElement timeline,
        IPatternEvidenceMaterializer? materializer = null)
    {
        var stub = new StubMatchClient { Match = MatchPayload(), Timeline = timeline };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = SelfPuuid });
        return new MapStateBackfillService(
            scope.Games, events, stub, config, NullLogger<MapStateBackfillService>.Instance, materializer);
    }

    private static VodSnapshotBuilder Snapshot(SidecarWriteScope scope, GameEventsRepository events) =>
        new(scope.Games, scope.Vod, events, scope.Evidence, scope.Objectives, scope.Config, NullLogger<VodSnapshotBuilder>.Instance);

    private static async Task<long> TrackAsync(SidecarWriteScope scope, string title, params string[] tokens)
    {
        var id = await scope.Objectives.CreateAsync(title);
        await scope.Objectives.SetEventTokensForObjectiveAsync(id, tokens);
        return id;
    }

    [Fact]
    public async Task PostGamePass_StoresOneTeamfightRow_AndVodRendersOnePinWithNumbers()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");

        // Capture-time rows from the live kill feed: two inside the fight, a death long after.
        await events.SaveEventsAsync(GameId,
        [
            new GameEvent { EventType = "KILL", GameTimeS = 600, Details = "{\"victim\":\"Zed\"}" },
            new GameEvent { EventType = "ASSIST", GameTimeS = 610, Details = "{\"victim\":\"Nocturne\"}" },
            new GameEvent { EventType = "DEATH", GameTimeS = 1200, Details = "{\"killer\":\"Garen\"}" },
        ]);

        var result = await Service(scope, events, OwnFightTimeline()).RunAsync(maxGames: 5);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Failed);

        var stream = await events.GetEventsAsync(GameId);
        var row = Assert.Single(stream, e => e.EventType == "TEAMFIGHT");
        Assert.True(row.Id > 0);
        var d = D(row);
        Assert.Equal("in", d.GetProperty("self").GetString());
        Assert.Equal("2v1", d.GetProperty("numbers").GetString());
        Assert.Equal("up", d.GetProperty("verdict").GetString());

        var vod = await Snapshot(scope, events).BuildAsync(GameId);

        Assert.DoesNotContain(vod.GameEvents, e => e.EventType == "TEAMFIGHT");
        // A user-confirmed encounter remains visible with its original identity.
        var confirmed = System.Text.Json.Nodes.JsonNode.Parse(row.Details)!.AsObject();
        confirmed["correction"] = new System.Text.Json.Nodes.JsonObject { ["fixture"] = true };
        await events.UpdateEventDetailsAsync(row.Id, confirmed.ToJsonString());
        vod = await Snapshot(scope, events).BuildAsync(GameId);
        var pin = Assert.Single(vod.GameEvents, e => e.EventType == "TEAMFIGHT");
        Assert.Equal(row.Id, pin.Id);
        Assert.Equal("2v1", pin.ShortLabel);
        Assert.Equal("teamfight", pin.Kind);
        Assert.Equal(600, pin.GameTimeSeconds);
        Assert.NotNull(pin.Teamfight);
        Assert.True(pin.Teamfight!.Stored);
        Assert.Equal("in", pin.Teamfight.Self);
        Assert.Equal(600, pin.Teamfight.StartSeconds);
        Assert.Equal(610, pin.Teamfight.EndSeconds);
        Assert.Equal("3v2", pin.Teamfight.Became);
        Assert.Equal(600, pin.Teamfight.EntrySeconds);
        Assert.Equal(3, pin.Teamfight.Kills);
        Assert.Equal(2, pin.Teamfight.KillsFor);
        Assert.Equal(1, pin.Teamfight.KillsAgainst);
        Assert.Equal(new[] { "Ahri", "Lee Sin" }, pin.Teamfight.Allies);
        Assert.Equal(new[] { "Zed" }, pin.Teamfight.Enemies);
        Assert.Equal("2v1 when you committed · became 3v2 · kills 2 for, 1 against · Ahri (you), Lee Sin vs Zed", pin.Summary);

        // The player's own combat rows still render as their own pins, on their real ids.
        Assert.Single(vod.GameEvents, e => e.EventType == "KILL");
        Assert.Single(vod.GameEvents, e => e.EventType == "ASSIST");
        Assert.Single(vod.GameEvents, e => e.EventType == "DEATH");
        Assert.DoesNotContain(vod.GameEvents, e => e.Id <= 0);
    }

    [Fact]
    public async Task PostGamePass_IsIdempotent_OnReRun()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        var service = Service(scope, events, OwnFightTimeline());

        Assert.Equal(1, (await service.RunAsync(maxGames: 5)).Updated);

        // Force the game back into the queue (what a future analyzer version does).
        await scope.Games.ClearMapStateVersionAsync(GameId);
        Assert.Equal(1, (await service.RunAsync(maxGames: 5)).Updated);

        var stream = await events.GetEventsAsync(GameId);
        Assert.Single(stream, e => e.EventType == "TEAMFIGHT");

        var vod = await Snapshot(scope, events).BuildAsync(GameId);
        Assert.DoesNotContain(vod.GameEvents, e => e.EventType == "TEAMFIGHT");
    }

    [Fact]
    public async Task AwayFight_RendersDimPin_WithoutTies_AndIsNeverClipped()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        await TrackAsync(scope, "Join fights with numbers", "TEAMFIGHT");

        Assert.Equal(1, (await Service(scope, events, AwayFightTimeline()).RunAsync(maxGames: 5)).Updated);

        var stream = await events.GetEventsAsync(GameId);
        var row = Assert.Single(stream, e => e.EventType == "TEAMFIGHT");
        Assert.Equal("away", D(row).GetProperty("self").GetString());

        Assert.DoesNotContain((await Snapshot(scope, events).BuildAsync(GameId)).GameEvents, e => e.EventType == "TEAMFIGHT");
        var confirmed = System.Text.Json.Nodes.JsonNode.Parse(row.Details)!.AsObject();
        confirmed["correction"] = new System.Text.Json.Nodes.JsonObject { ["fixture"] = true };
        await events.UpdateEventDetailsAsync(row.Id, confirmed.ToJsonString());

        var vod = await Snapshot(scope, events).BuildAsync(GameId);
        var pin = Assert.Single(vod.GameEvents, e => e.EventType == "TEAMFIGHT");
        Assert.Equal("teamfight-away", pin.Kind);
        Assert.Equal("TF", pin.ShortLabel);
        Assert.Equal("Fight without you", pin.Label);
        Assert.Null(pin.ObjectiveId);
        Assert.Null(pin.ObjectiveIds);
        Assert.Equal(row.Id, pin.Id);
        Assert.NotNull(pin.Teamfight);
        Assert.True(pin.Teamfight!.Stored);
        Assert.Equal("away", pin.Teamfight.Self);
        Assert.Equal("2v2", pin.Teamfight.Numbers);
        Assert.Equal("2v2 fight you were not in · kills 2 for, 1 against · Lee Sin, Jinx vs Zed, Nocturne", pin.Summary);

        var resolver = ObjectiveEventTieResolver.FromTies(await scope.Objectives.GetActiveObjectiveEventTokensAsync());
        var clips = AutoClipPlanner.SelectClips(GameId, stream, resolver, null, 1800, new HashSet<string>(), out _);
        Assert.Empty(clips);
    }

    [Fact]
    public async Task VerdictOnlyObjective_TiesOnlyMatchingFights()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        var objectiveId = await TrackAsync(scope, "Stop taking outnumbered fights", "OUTNUMBERED_TEAMFIGHT");

        Assert.Equal(1, (await Service(scope, events, DownThenUpTimeline()).RunAsync(maxGames: 5)).Updated);

        var stream = await events.GetEventsAsync(GameId);
        Assert.Equal(2, stream.Count(e => e.EventType == "TEAMFIGHT"));

        Assert.DoesNotContain((await Snapshot(scope, events).BuildAsync(GameId)).GameEvents, e => e.EventType == "TEAMFIGHT");
        foreach (var row in stream.Where(e => e.EventType == "TEAMFIGHT"))
        {
            var confirmed = System.Text.Json.Nodes.JsonNode.Parse(row.Details)!.AsObject();
            confirmed["correction"] = new System.Text.Json.Nodes.JsonObject { ["fixture"] = true };
            await events.UpdateEventDetailsAsync(row.Id, confirmed.ToJsonString());
        }

        var vod = await Snapshot(scope, events).BuildAsync(GameId);
        var pins = vod.GameEvents.Where(e => e.EventType == "TEAMFIGHT").ToList();
        Assert.Equal(2, pins.Count);

        var down = Assert.Single(pins, p => p.Teamfight!.Verdict == "down");
        var up = Assert.Single(pins, p => p.Teamfight!.Verdict == "up");
        Assert.Equal("1v3", down.ShortLabel);
        Assert.Equal(objectiveId, down.ObjectiveId);
        Assert.Equal(new[] { objectiveId }, down.ObjectiveIds);
        Assert.Equal("2v1", up.ShortLabel);
        Assert.Null(up.ObjectiveId);
        Assert.Null(up.ObjectiveIds);
        Assert.DoesNotContain(pins, p => p.Id <= 0);
    }

    [Fact]
    public async Task MapStatePass_ReMaterializesFightAnchors()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        await TrackAsync(scope, "Join fights with numbers", "TEAMFIGHT");

        var materializer = new PatternEvidenceMaterializer(
            events, scope.Evidence, scope.Objectives, scope.Games, NullLogger<PatternEvidenceMaterializer>.Instance);

        Assert.Equal(1, (await Service(scope, events, OwnFightTimeline(), materializer).RunAsync(maxGames: 5)).Updated);

        var rows = await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true);
        Assert.DoesNotContain(rows, r => r.SourceKey.StartsWith("objev:TEAMFIGHT:", StringComparison.Ordinal));
    }
}
