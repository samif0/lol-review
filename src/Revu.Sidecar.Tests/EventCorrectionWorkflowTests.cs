using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The sidecar's write seam for the v3.11 corrections ledger: <see cref="EventCorrectionWorkflow"/>
/// runs rule F (derived-event recompute + pattern re-materialize) after every fix and revert
/// that changed something, the legacy /api/encounter/save alias still returns a row id and
/// lands in the ledger, and a corrected fight survives the post-game map-state pass re-running
/// on top of it. Real repositories against a temp DB; only the Riot HTTP hop is stubbed.
/// </summary>
public sealed class EventCorrectionWorkflowTests
{
    private const long GameId = 5_598_958_690;
    private const string SelfPuuid = "self-puuid";

    private sealed class ThrowingMaterializer : IPatternEvidenceMaterializer
    {
        public int Calls;
        public Task MaterializeForGameAsync(long gameId) { Calls++; throw new InvalidOperationException("materializer down"); }
        public Task MaterializeReviewSignalsAsync(long gameId) => throw new InvalidOperationException("materializer down");
        public Task<int> BackfillWindowAsync() => throw new InvalidOperationException("materializer down");
    }

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

    // Three kills within 14 s at one spot, the player credited on the first and last; the
    // junglers parked far away so the jungle leg derives nothing.
    private static JsonElement OwnFightTimeline() => Parse($$"""
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
              "events": [
                { "type": "CHAMPION_KILL", "timestamp": 600000, "killerId": 1, "victimId": 6, "assistingParticipantIds": [2], "position": { "x": 7000, "y": 7000 } },
                { "type": "CHAMPION_KILL", "timestamp": 605000, "killerId": 7, "victimId": 3, "assistingParticipantIds": [], "position": { "x": 7000, "y": 7000 } },
                { "type": "CHAMPION_KILL", "timestamp": 610000, "killerId": 2, "victimId": 7, "assistingParticipantIds": [1], "position": { "x": 7000, "y": 7000 } }
              ] }
        ] } }
        """);

    // The enemy laner (6) kills the player at 5:00 with both junglers parked far away: the
    // jungle leg stamps the death (map_state, distances, darkness) and decides "not a fog
    // death"; nothing else is derived.
    private static JsonElement LanerKillTimeline() => Parse($$"""
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
                { "type": "CHAMPION_KILL", "timestamp": 300000, "killerId": 6, "victimId": 1, "assistingParticipantIds": [], "position": { "x": 7000, "y": 5000 } }
              ] }
        ] } }
        """);

    private static GameEvent Ev(string type, int t, string details = "{}") =>
        new() { GameId = GameId, EventType = type, GameTimeS = t, Details = details };

    private static EventCorrectionSubject Subject(GameEvent row) => new(row.EventKey, row.Id, row.EventType, row.GameTimeS);

    private static EventCorrectionRequest Req(string op, EventCorrectionSubject? subject, EventPatch? patch = null, string reason = "") =>
        new(GameId, Guid.NewGuid().ToString("D"), op, subject, patch ?? EventPatch.Empty, reason, "3.11.0");

    private static EventCorrectionWorkflow Workflow(
        SidecarWriteScope scope, GameEventsRepository events, DerivedEventsRepository derived, IPatternEvidenceMaterializer materializer) =>
        new(new EventCorrectionsRepository(scope.ConnectionFactory), events, derived, materializer,
            NullLogger<EventCorrectionWorkflow>.Instance);

    private static PatternEvidenceMaterializer RealMaterializer(SidecarWriteScope scope, GameEventsRepository events) =>
        new(events, scope.Evidence, scope.Objectives, scope.Games, NullLogger<PatternEvidenceMaterializer>.Instance);

    private static MapStateBackfillService Service(SidecarWriteScope scope, GameEventsRepository events, JsonElement timeline)
    {
        var stub = new StubMatchClient { Match = MatchPayload(), Timeline = timeline };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = SelfPuuid });
        return new MapStateBackfillService(scope.Games, events, stub, config, NullLogger<MapStateBackfillService>.Instance);
    }

    private static VodSnapshotBuilder Snapshot(SidecarWriteScope scope, GameEventsRepository events, IEventCorrectionsRepository ledger) =>
        new(scope.Games, scope.Vod, events, scope.Evidence, scope.Objectives, scope.Config,
            NullLogger<VodSnapshotBuilder>.Instance, ledger);

    // What capture does after a save: compute + persist the derived instances once.
    private static async Task RecomputeAsync(GameEventsRepository events, DerivedEventsRepository derived)
    {
        var instances = derived.ComputeInstances(GameId, await events.GetEventsAsync(GameId), await derived.GetAllDefinitionsAsync());
        await derived.SaveInstancesAsync(GameId, instances);
    }

    [Fact]
    public async Task Save_RecomputesDerivedInstances_EvenWhenResultIsEmpty()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        var derived = new DerivedEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId);

        // Two deaths 40 s apart = one "Death Streak" instance (2 deaths within 60 s).
        await events.SaveEventsAsync(GameId, [Ev("DEATH", 300, "{\"killer\":\"Lee Sin\"}"), Ev("DEATH", 340, "{\"killer\":\"Zed\"}")]);
        await RecomputeAsync(events, derived);
        Assert.NotEmpty(await derived.GetInstancesAsync(GameId));

        var second = Assert.Single(await events.GetEventsAsync(GameId), e => e.GameTimeS == 340);
        var workflow = Workflow(scope, events, derived, RealMaterializer(scope, events));
        var request = Req(CorrectionOps.Remove, Subject(second), reason: "Not a death");
        var r = await workflow.SaveAsync(request);

        Assert.False(r.Idempotent);
        Assert.Equal(CorrectionStates.Active, r.State);
        Assert.Null(r.AppliedEventId);
        // Rule F wrote the EMPTY result: the streak no longer exists, its row is gone.
        Assert.Empty(await derived.GetInstancesAsync(GameId));
        Assert.Equal(300, Assert.Single(await events.GetEventsAsync(GameId)).GameTimeS);

        // A replay of the same correction id changes nothing and refreshes nothing.
        var again = await workflow.SaveAsync(request);
        Assert.True(again.Idempotent);
        Assert.Equal(r.CorrectionId, again.CorrectionId);
        Assert.Empty(await derived.GetInstancesAsync(GameId));
    }

    [Fact]
    public async Task Save_AndRevert_RunRuleF_BestEffortWhenMaterializerThrows()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        var derived = new DerivedEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId);
        await events.SaveEventsAsync(GameId, [Ev("DEATH", 300, "{\"killer\":\"Lee Sin\"}"), Ev("DEATH", 340, "{\"killer\":\"Zed\"}")]);
        await RecomputeAsync(events, derived);
        Assert.NotEmpty(await derived.GetInstancesAsync(GameId));

        var materializer = new ThrowingMaterializer();
        var workflow = Workflow(scope, events, derived, materializer);
        var second = Assert.Single(await events.GetEventsAsync(GameId), e => e.GameTimeS == 340);

        // Retime the second death out of the 60 s window: the fix lands, the derived
        // recompute runs (empty result), the materializer's throw is swallowed.
        var r = await workflow.SaveAsync(Req(CorrectionOps.Retime, Subject(second), new EventPatch(null, 500, null, null), "Clock was off"));
        Assert.False(r.Idempotent);
        Assert.Equal(1, materializer.Calls);
        Assert.Equal(500, Assert.Single(await events.GetEventsAsync(GameId), e => e.Id == second.Id).GameTimeS);
        Assert.Empty(await derived.GetInstancesAsync(GameId));

        // Revert restores the row and rule F runs again, still best-effort.
        var reverted = await workflow.RevertAsync(GameId, r.CorrectionId, "Undo", "3.11.0");
        Assert.False(reverted.Idempotent);
        Assert.Equal(CorrectionStates.Reverted, reverted.State);
        Assert.Equal(2, materializer.Calls);
        Assert.Equal(340, Assert.Single(await events.GetEventsAsync(GameId), e => e.Id == second.Id).GameTimeS);
        Assert.NotEmpty(await derived.GetInstancesAsync(GameId));

        // Reverting a reverted correction is idempotent and does not refresh.
        var again = await workflow.RevertAsync(GameId, r.CorrectionId, "", "3.11.0");
        Assert.True(again.Idempotent);
        Assert.Equal(2, materializer.Calls);
    }

    [Fact]
    public async Task EncounterSaveAlias_StillReturnsRowId_AndRefreshes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        var derived = new DerivedEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId);
        await events.SaveEventsAsync(GameId, [Ev("KILL", 600, "{\"victim\":\"Zed\"}")]);

        // What POST /api/encounter/save does: the legacy repository (which now delegates
        // to the ledger), then the workflow's rule F.
        var encounters = new ReviewedEncountersRepository(scope.ConnectionFactory);
        var requestId = Guid.NewGuid().ToString("D");
        var id = await encounters.SaveAsync(GameId, null, requestId, 100, 130, "short", "Traded well");
        Assert.True(id > 0);
        var ledger = new EventCorrectionsRepository(scope.ConnectionFactory);
        await Workflow(scope, events, derived, RealMaterializer(scope, events)).RefreshDerivedAsync(GameId);

        var vod = await Snapshot(scope, events, ledger).BuildAsync(GameId);
        var trade = Assert.Single(vod.GameEvents, e => e.EventType == "TRADE");
        Assert.Equal(id, trade.Id);
        Assert.True(trade.AddedByUser);
        Assert.False(trade.Corrected);
        Assert.True(trade.ReviewedEncounter);
        Assert.Equal("short", trade.EncounterClassification);
        Assert.Equal(130, trade.EncounterEndSeconds);
        Assert.Equal("Traded well", trade.EncounterNote);
        Assert.Equal(EventIdentity.UserKey(requestId), trade.EventKey);
        Assert.Equal(CorrectionOps.Add, trade.CorrectionOp);

        var row = Assert.Single(vod.Corrections!);
        Assert.Equal(requestId, row.CorrectionId);
        Assert.Equal(CorrectionOps.Add, row.Op);
        Assert.Equal("Trade added at 1:40", row.Summary);
        Assert.Equal(1, await ledger.CountActiveForGameAsync(GameId));

        // The legacy retry contract: the same request id returns the same row id.
        Assert.Equal(id, await encounters.SaveAsync(GameId, null, requestId, 100, 130, "short", "Traded well"));
        Assert.Single(await ledger.GetForGameAsync(GameId));
    }

    [Fact]
    public async Task MapStateRerunAfterFightRetime_KeepsTheFix()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        var derived = new DerivedEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        await events.SaveEventsAsync(GameId,
        [
            Ev("KILL", 600, "{\"victim\":\"Zed\"}"),
            Ev("ASSIST", 610, "{\"victim\":\"Nocturne\"}"),
            Ev("DEATH", 1200, "{\"killer\":\"Garen\"}"),
        ]);
        var service = Service(scope, events, OwnFightTimeline());
        Assert.Equal(1, (await service.RunAsync(maxGames: 5)).Updated);

        var fight = Assert.Single(await events.GetEventsAsync(GameId), e => e.EventType == "TEAMFIGHT");
        Assert.Equal(600, fight.GameTimeS);
        Assert.Equal("det:TEAMFIGHT:600:", fight.EventKey);

        // The reviewer says the fight started 2 s earlier.
        var ledger = new EventCorrectionsRepository(scope.ConnectionFactory);
        var workflow = Workflow(scope, events, derived, RealMaterializer(scope, events));
        var r = await workflow.SaveAsync(Req(CorrectionOps.Retime, Subject(fight), new EventPatch(null, 598, null, null), "Fight started on the engage"));
        Assert.Equal(CorrectionStates.Active, r.State);
        Assert.Equal(fight.Id, r.AppliedEventId);

        // A detector bump re-runs the post-game pass (delete by type + append the twin).
        await scope.Games.ClearMapStateVersionAsync(GameId);
        Assert.Equal(1, (await service.RunAsync(maxGames: 5)).Updated);

        var kept = Assert.Single(await events.GetEventsAsync(GameId), e => e.EventType == "TEAMFIGHT");
        Assert.Equal(fight.Id, kept.Id);
        Assert.Equal(598, kept.GameTimeS);
        using (var doc = JsonDocument.Parse(kept.Details))
        {
            Assert.Equal(598, doc.RootElement.GetProperty("start_s").GetInt32());
            Assert.Equal(610, doc.RootElement.GetProperty("end_s").GetInt32());
            Assert.Equal(r.CorrectionId, doc.RootElement.GetProperty("correction").GetProperty("id").GetString());
        }

        // The twin (start 600) is 2 s from the fix: suppressed, the correction stays active
        // (absorbed needs the detector within 1 s), still bound to the same row.
        var c = Assert.Single(await ledger.GetActiveForGameAsync(GameId));
        Assert.Equal(r.CorrectionId, c.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(fight.Id, c.AppliedEventId);
        Assert.Equal("det:TEAMFIGHT:600:", c.SubjectKey);

        var vod = await Snapshot(scope, events, ledger).BuildAsync(GameId);
        var pin = Assert.Single(vod.GameEvents, e => e.EventType == "TEAMFIGHT");
        Assert.Equal(fight.Id, pin.Id);
        // The pin keeps anchoring on the earliest member combat event (the KILL at 600, the
        // TeamfightSpan contract); the fight's own window is what the retime moved.
        Assert.Equal(600, pin.GameTimeSeconds);
        Assert.Equal(598, pin.Teamfight!.StartSeconds);
        Assert.Equal(610, pin.Teamfight.EndSeconds);
        Assert.True(pin.Corrected);
        Assert.Equal(CorrectionOps.Retime, pin.CorrectionOp);
        Assert.Equal(CorrectionStates.Active, pin.CorrectionState);
        Assert.Equal("det:TEAMFIGHT:600:", pin.EventKey);
    }

    [Fact]
    public async Task MapStateRerunAfterFogDeathFix_KeepsTheFix_AndDoesNotReportItAbsorbed()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var events = new GameEventsRepository(scope.ConnectionFactory);
        var derived = new DerivedEventsRepository(scope.ConnectionFactory);
        await scope.SeedGameAsync(GameId, champion: "Ahri");
        await events.SaveEventsAsync(GameId, [Ev("DEATH", 300, "{\"killer\":\"Zed\"}")]);
        var service = Service(scope, events, LanerKillTimeline());
        Assert.Equal(1, (await service.RunAsync(maxGames: 5)).Updated);

        var death = Assert.Single(await events.GetEventsAsync(GameId));
        using (var doc = JsonDocument.Parse(death.Details))
        {
            Assert.True(doc.RootElement.GetProperty("map_state").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("fog_death", out _));
        }

        // The reviewer says it WAS a fog death (the detector missed it).
        var ledger = new EventCorrectionsRepository(scope.ConnectionFactory);
        var workflow = Workflow(scope, events, derived, RealMaterializer(scope, events));
        var attrs = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["fog_death"] = System.Text.Json.Nodes.JsonValue.Create(true) };
        var r = await workflow.SaveAsync(Req(CorrectionOps.Attr, Subject(death), new EventPatch(null, null, null, attrs), "Never saw him"));
        Assert.Equal(CorrectionStates.Active, r.State);

        // A detector bump re-runs the pass: the stamp re-parses the corrected row.
        await scope.Games.ClearMapStateVersionAsync(GameId);
        Assert.Equal(1, (await service.RunAsync(maxGames: 5)).Updated);

        var kept = Assert.Single(await events.GetEventsAsync(GameId));
        Assert.Equal(death.Id, kept.Id);
        using (var doc = JsonDocument.Parse(kept.Details))
        {
            Assert.True(doc.RootElement.GetProperty("fog_death").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("map_state").GetBoolean());
            Assert.Equal(300, doc.RootElement.GetProperty("enemy_jg_dark_s").GetInt32());
            Assert.Equal(r.CorrectionId, doc.RootElement.GetProperty("correction").GetProperty("id").GetString());
        }
        // The detector still misses it, so the fix is applied, not absorbed.
        var c = Assert.Single(await ledger.GetActiveForGameAsync(GameId));
        Assert.Equal(r.CorrectionId, c.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(death.Id, c.AppliedEventId);
    }
}
