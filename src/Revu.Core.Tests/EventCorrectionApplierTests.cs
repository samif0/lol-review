using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Rule A of the corrections ledger: a capture-time re-save (SaveEventsAsync) keeps the corrected
/// row and its id, suppresses the detector's twin, flips the correction to absorbed when the
/// detector reproduced the fix, orphans it when the detector no longer sees the event, and
/// re-applies the patch when the corrected row itself is gone. Rules B and C live in the sibling
/// partial; helpers are here.
/// </summary>
public sealed partial class EventCorrectionApplierTests
{
    private const long GameId = 7101;
    private const string LeeDeath = "{\"killer\":\"Lee Sin\"}";

    private static GameEvent Ev(string type, int t, string details = "{}") =>
        new() { GameId = GameId, EventType = type, GameTimeS = t, Details = details };

    private static GameEvent Fight(int startS, int endS, string self = "in", string verdict = "down", string numbers = "2v3") =>
        Ev("TEAMFIGHT", startS,
            $$"""{ "detected": true, "v": 1, "start_s": {{startS}}, "end_s": {{endS}}, "self": "{{self}}", "numbers": "{{numbers}}", "verdict": "{{verdict}}" }""");

    private static Dictionary<string, JsonNode?> Attrs(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in pairs) d[k] = JsonValue.Create(v);
        return d;
    }

    private static EventCorrectionSubject Subject(GameEvent row) => new(row.EventKey, row.Id, row.EventType, row.GameTimeS);

    private static EventCorrectionRequest Req(string op, EventCorrectionSubject? subject, EventPatch? patch = null, string reason = "") =>
        new(GameId, Guid.NewGuid().ToString("D"), op, subject, patch ?? EventPatch.Empty, reason, "3.11.0");

    private static async Task<(EventCorrectionsRepository Repo, List<GameEvent> Rows)> SeedAsync(TestDatabaseScope scope, params GameEvent[] events)
    {
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(GameId));
        if (events.Length > 0) await scope.GameEvents.SaveEventsAsync(GameId, events);
        return (new EventCorrectionsRepository(scope.ConnectionFactory), await RowsAsync(scope));
    }

    private static async Task<List<GameEvent>> RowsAsync(TestDatabaseScope scope)
    {
        using var conn = scope.OpenConnection();
        return await EventCorrectionSql.LoadRowsAsync(conn, null, GameId);
    }

    private static async Task<EventCorrection> LedgerAsync(EventCorrectionsRepository repo, string correctionId) =>
        (await repo.GetForGameAsync(GameId)).Single(c => c.CorrectionId == correctionId);

    private static JsonObject Details(GameEvent row) => (JsonObject)JsonNode.Parse(row.Details)!;

    [Fact]
    public async Task RuleA_TwinIsSuppressed_AndCorrectedIdSurvivesTwoRecaptures_IncludingSelfRoundTrip()
    {
        using var scope = new TestDatabaseScope();
        var captured = new[] { Ev("DEATH", 812, LeeDeath), Ev("KILL", 900, "{\"victim\":\"Jinx\"}") };
        var (repo, rows) = await SeedAsync(scope, captured);
        var death = rows.Single(x => x.EventType == "DEATH");
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(death), new EventPatch(null, 815, null, null)));

        // A second capture of the same feed.
        await scope.GameEvents.SaveEventsAsync(GameId, captured);
        var after = await RowsAsync(scope);
        Assert.Equal(2, after.Count);
        var kept = Assert.Single(after, x => x.EventType == "DEATH");
        Assert.Equal(death.Id, kept.Id);
        Assert.Equal(815, kept.GameTimeS);
        Assert.Equal("det:DEATH:812:Lee Sin", kept.EventKey);
        Assert.Equal((r.CorrectionId, "retime"), EventPatching.ReadMarker(kept.Details));
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(death.Id, c.AppliedEventId);

        // The table sent back to itself (the corrected row is part of the batch).
        await scope.GameEvents.SaveEventsAsync(GameId, await scope.GameEvents.GetEventsAsync(GameId));
        after = await RowsAsync(scope);
        Assert.Equal(2, after.Count);
        kept = Assert.Single(after, x => x.EventType == "DEATH");
        Assert.Equal(death.Id, kept.Id);
        Assert.Equal(815, kept.GameTimeS);
        Assert.Equal("det:KILL:900:Jinx", Assert.Single(after, x => x.EventType == "KILL").EventKey);
        c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(death.Id, c.AppliedEventId);
        Assert.Equal("", c.ApplyError);
    }

    [Fact]
    public async Task RuleA_RetimeSatisfiedByDetector_FlipsAbsorbed_AndStillSuppresses()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, LeeDeath));
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));

        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 815, LeeDeath)]);

        var kept = Assert.Single(await RowsAsync(scope));
        Assert.Equal(rows[0].Id, kept.Id);
        Assert.Equal(815, kept.GameTimeS);
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Absorbed, c.State);
        Assert.Equal(rows[0].Id, c.AppliedEventId);
        Assert.Single(await repo.GetActiveForGameAsync(GameId));
    }

    [Fact]
    public async Task RuleA_FogDeathFalse_DetectorNoLongerStamps_FlipsAbsorbed()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"map_state\":true,\"fog_death\":true}"));
        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("fog_death", false)))));

        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 812, LeeDeath)]);

        var kept = Assert.Single(await RowsAsync(scope));
        Assert.Equal(rows[0].Id, kept.Id);
        Assert.False(Details(kept)["fog_death"]!.GetValue<bool>());
        Assert.Equal(CorrectionStates.Absorbed, (await LedgerAsync(repo, r.CorrectionId)).State);
    }

    [Fact]
    public async Task RuleA_NoTwinInScope_MarksOrphaned_KeepsRow()
    {
        using var scope = new TestDatabaseScope();
        var captured = new[] { Ev("DEATH", 812, LeeDeath), Ev("KILL", 900) };
        var (repo, rows) = await SeedAsync(scope, captured);
        var death = rows.Single(x => x.EventType == "DEATH");
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(death), new EventPatch(null, 815, null, null)));

        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("KILL", 900)]);

        var after = await RowsAsync(scope);
        Assert.Equal(2, after.Count);
        var kept = Assert.Single(after, x => x.EventType == "DEATH");
        Assert.Equal(death.Id, kept.Id);
        Assert.Equal(815, kept.GameTimeS);
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Orphaned, c.State);
        Assert.Equal(death.Id, c.AppliedEventId);
        Assert.Contains(c, await repo.GetActiveForGameAsync(GameId));

        // The detector sees the death again: the correction is simply active once more.
        await scope.GameEvents.SaveEventsAsync(GameId, captured);
        Assert.Equal(2, (await RowsAsync(scope)).Count);
        Assert.Equal(CorrectionStates.Active, (await LedgerAsync(repo, r.CorrectionId)).State);
    }

    [Fact]
    public async Task RuleA_AppliedRowMissing_ReinsertsPatchedTwin_AndRebindsAppliedEventId()
    {
        using var scope = new TestDatabaseScope();
        var captured = new[] { Ev("DEATH", 812, LeeDeath), Ev("KILL", 900) };
        var (repo, rows) = await SeedAsync(scope, captured);
        var death = rows.Single(x => x.EventType == "DEATH");
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(death), new EventPatch(null, 815, null, null), "Clock drift"));

        await scope.GameEvents.DeleteEventsAsync(GameId);
        await scope.GameEvents.SaveEventsAsync(GameId, captured);

        var after = await RowsAsync(scope);
        Assert.Equal(2, after.Count);
        var reinserted = Assert.Single(after, x => x.EventType == "DEATH");
        Assert.NotEqual(death.Id, reinserted.Id);
        Assert.Equal(815, reinserted.GameTimeS);
        Assert.Equal("det:DEATH:812:Lee Sin", reinserted.EventKey);
        Assert.Equal("Lee Sin", Details(reinserted)["killer"]!.GetValue<string>());
        Assert.Equal((r.CorrectionId, "retime"), EventPatching.ReadMarker(reinserted.Details));
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(reinserted.Id, c.AppliedEventId);
    }

    [Fact]
    public async Task RuleA_MissingAddRowIsReinserted()
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope, Ev("KILL", 900));
        var r = await repo.SaveAsync(Req(CorrectionOps.Add, null, new EventPatch("DRAGON", 1200, null, null), "Missed by the feed"));

        await scope.GameEvents.DeleteEventsAsync(GameId);
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("KILL", 900)]);

        var after = await RowsAsync(scope);
        Assert.Equal(2, after.Count);
        var dragon = Assert.Single(after, x => x.EventType == "DRAGON");
        Assert.Equal(1200, dragon.GameTimeS);
        Assert.Equal("usr:" + r.CorrectionId, dragon.EventKey);
        Assert.Equal((r.CorrectionId, "add"), EventPatching.ReadMarker(dragon.Details));
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(dragon.Id, c.AppliedEventId);

        // The re-inserted row now survives the replace like any corrected row; no duplicate.
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("KILL", 900)]);
        Assert.Equal(dragon.Id, Assert.Single(await RowsAsync(scope), x => x.EventType == "DRAGON").Id);
    }

    [Fact]
    public async Task RuleA_RemoveSuppressesTwin_AndAbsorbsWhenTwinVanishes()
    {
        using var scope = new TestDatabaseScope();
        var captured = new[] { Ev("DEATH", 812, LeeDeath), Ev("KILL", 900) };
        var (repo, rows) = await SeedAsync(scope, captured);
        var r = await repo.SaveAsync(Req(CorrectionOps.Remove, Subject(rows.Single(x => x.EventType == "DEATH")), reason: "Not a death"));

        await scope.GameEvents.SaveEventsAsync(GameId, captured);
        Assert.Single(await RowsAsync(scope), x => x.EventType == "KILL");
        Assert.DoesNotContain(await RowsAsync(scope), x => x.EventType == "DEATH");
        Assert.Equal(CorrectionStates.Active, (await LedgerAsync(repo, r.CorrectionId)).State);

        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("KILL", 900)]);
        Assert.Single(await RowsAsync(scope));
        Assert.Equal(CorrectionStates.Absorbed, (await LedgerAsync(repo, r.CorrectionId)).State);

        await scope.GameEvents.SaveEventsAsync(GameId, captured);
        Assert.Single(await RowsAsync(scope));
        Assert.Equal(CorrectionStates.Active, (await LedgerAsync(repo, r.CorrectionId)).State);
    }

    [Fact]
    public async Task RuleA_FuzzyMatchRebasesSubjectKey_AndSurvivorKey()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"fog_death\":true}"));
        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("fog_death", false)))));

        // The collector's clock drifted by a second on the re-capture.
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 813, "{\"killer\":\"Lee Sin\",\"fog_death\":true}")]);

        var kept = Assert.Single(await RowsAsync(scope));
        Assert.Equal(rows[0].Id, kept.Id);
        Assert.False(Details(kept)["fog_death"]!.GetValue<bool>());
        Assert.Equal("det:DEATH:813:Lee Sin", kept.EventKey);
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal("det:DEATH:813:Lee Sin", c.SubjectKey);
        Assert.Equal("det:DEATH:812:Lee Sin", c.RebasedFrom);
        Assert.Equal(CorrectionStates.Active, c.State);

        // The rebased key is now the exact one.
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 813, "{\"killer\":\"Lee Sin\",\"fog_death\":true}")]);
        Assert.Equal(rows[0].Id, Assert.Single(await RowsAsync(scope)).Id);
    }

    [Fact]
    public async Task RuleA_LegacyReviewedWindowSuppression_StillApplies()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("TRADE", 110, "{\"detected\":true,\"kind\":\"extended\"}"), Ev("KILL", 115));
        var trade = rows.Single(x => x.EventType == "TRADE");
        await repo.SaveAsync(Req(CorrectionOps.Retype, Subject(trade), new EventPatch("ALL_IN", 100, 114, null), "Committed pursuit"));

        // The twin at 110 is the correction's subject; the trade at 105 falls inside the reviewed window.
        await scope.GameEvents.SaveEventsAsync(GameId,
        [
            Ev("TRADE", 110, "{\"detected\":true,\"kind\":\"extended\"}"),
            Ev("TRADE", 105, "{\"detected\":true,\"kind\":\"short\"}"),
            Ev("KILL", 115),
        ]);

        var after = await RowsAsync(scope);
        Assert.Equal(2, after.Count);
        var allIn = Assert.Single(after, x => x.EventType == "ALL_IN");
        Assert.Equal(trade.Id, allIn.Id);
        Assert.Equal(100, allIn.GameTimeS);
        Assert.Single(after, x => x.EventType == "KILL");

        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "ALL_IN");
        Assert.Single(await RowsAsync(scope), x => x.EventType == "ALL_IN");
    }
}
