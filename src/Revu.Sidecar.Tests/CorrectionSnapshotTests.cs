using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// What the read snapshots expose for the v3.11 corrections ledger: the VOD snapshot
/// carries the correctable-type catalog, this game's corrections list, the stable event
/// key on every marker, the fixed / added / confirmed decoration, the ghost of a removed
/// event, and still builds without a ledger (the seven-argument ctor); the review snapshot
/// stamps event key + row id on every death row and counts timeline fixes in the header.
/// </summary>
public sealed class CorrectionSnapshotTests
{
    private const long GameId = 7_001;
    private const string LeeDeath = "{\"killer\":\"Lee Sin\"}";

    private static GameEvent Ev(string type, int t, string details = "{}") =>
        new() { GameId = GameId, EventType = type, GameTimeS = t, Details = details };

    private static GameEvent Fight(int startS, int endS, string self = "in", string verdict = "down", string numbers = "2v3") =>
        Ev("TEAMFIGHT", startS,
            $$"""{ "detected": true, "v": 1, "start_s": {{startS}}, "end_s": {{endS}}, "self": "{{self}}", "numbers": "{{numbers}}", "verdict": "{{verdict}}" }""");

    private static EventCorrectionSubject Subject(GameEvent row) => new(row.EventKey, row.Id, row.EventType, row.GameTimeS);

    private static EventCorrectionRequest Req(string op, EventCorrectionSubject? subject, EventPatch? patch = null, string reason = "") =>
        new(GameId, Guid.NewGuid().ToString("D"), op, subject, patch ?? EventPatch.Empty, reason, "3.11.0");

    private static EventPatch Attr(string key, JsonNode? value) =>
        new(null, null, null, new Dictionary<string, JsonNode?> { [key] = value });

    private static VodSnapshotBuilder Vod(SidecarWriteScope scope, GameEventsRepository events, IEventCorrectionsRepository? ledger) =>
        new(scope.Games, scope.Vod, events, scope.Evidence, scope.Objectives, scope.Config,
            NullLogger<VodSnapshotBuilder>.Instance, ledger);

    private static ReviewSnapshotBuilder Review(SidecarWriteScope scope, GameEventsRepository events, IEventCorrectionsRepository? ledger) =>
        new(scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog, scope.Evidence, events,
            scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod, scope.Config, scope.ReviewDrafts,
            NullLogger<ReviewSnapshotBuilder>.Instance, ledger);

    private static async Task<(GameEventsRepository Events, EventCorrectionsRepository Ledger)> SeedAsync(SidecarWriteScope scope, params GameEvent[] events)
    {
        await scope.InitializeAsync();
        await scope.SeedGameAsync(GameId);
        var repo = new GameEventsRepository(scope.ConnectionFactory);
        if (events.Length > 0) await repo.SaveEventsAsync(GameId, events);
        return (repo, new EventCorrectionsRepository(scope.ConnectionFactory));
    }

    private static async Task<GameEvent> RowAsync(GameEventsRepository events, string type, int t) =>
        Assert.Single(await events.GetEventsAsync(GameId), e => e.EventType == type && e.GameTimeS == t);

    [Fact]
    public async Task VodSnapshot_ExposesCatalog_Corrections_AndEventKeys()
    {
        using var scope = new SidecarWriteScope();
        var (events, ledger) = await SeedAsync(scope,
            Ev("KILL", 200, "{\"victim\":\"Zed\"}"), Ev("DEATH", 300, LeeDeath), Ev("DRAGON", 900, "{\"dragon_type\":\"Infernal\"}"));

        var vod = await Vod(scope, events, ledger).BuildAsync(GameId);

        // The catalog is the Core catalog, verbatim, so JS never hardcodes a type or value.
        Assert.NotNull(vod.EventTypeCatalog);
        Assert.Equal(EventCorrectionCatalog.Types.Count, vod.EventTypeCatalog!.Count);
        var death = Assert.Single(vod.EventTypeCatalog, t => t.Type == "DEATH");
        Assert.Equal("point", death.Kind);
        Assert.Equal(new[] { "jungle_gank", "fog_death" }, death.Attrs.Select(a => a.Key));
        Assert.All(death.Attrs, a => Assert.Equal("bool", a.Input));
        var trade = Assert.Single(vod.EventTypeCatalog, t => t.Type == "TRADE");
        Assert.Equal("span", trade.Kind);
        var kind = Assert.Single(trade.Attrs);
        Assert.Equal("choice", kind.Input);
        Assert.Equal(new[] { "short", "extended" }, kind.Options.Select(o => o.Value));
        Assert.NotNull(vod.Corrections);
        Assert.Empty(vod.Corrections!);

        // Every marker carries its stable identity, untouched rows are undecorated.
        Assert.Equal(3, vod.GameEvents.Count);
        Assert.All(vod.GameEvents, e =>
        {
            Assert.StartsWith("det:", e.EventKey);
            Assert.False(e.Corrected);
            Assert.False(e.AddedByUser);
            Assert.False(e.Removed);
            Assert.Equal("", e.CorrectionState);
        });
        Assert.Equal("det:DEATH:300:Lee Sin", Assert.Single(vod.GameEvents, e => e.EventType == "DEATH").EventKey);

        // One retime lands in the list with its human summary.
        var r = await ledger.SaveAsync(Req(CorrectionOps.Retime, Subject(await RowAsync(events, "DEATH", 300)),
            new EventPatch(null, 305, null, null), "Clock was off"));
        vod = await Vod(scope, events, ledger).BuildAsync(GameId);
        var row = Assert.Single(vod.Corrections!);
        Assert.Equal(r.CorrectionId, row.CorrectionId);
        Assert.Equal(CorrectionOps.Retime, row.Op);
        Assert.Equal(CorrectionStates.Active, row.State);
        Assert.Equal("applied", row.StateLabel);
        Assert.Equal("det:DEATH:300:Lee Sin", row.SubjectKey);
        Assert.Equal("DEATH", row.SubjectType);
        Assert.Equal(300, row.SubjectTimeSeconds);
        Assert.Equal("DEATH", row.EventType);
        Assert.Equal(305, row.GameTimeSeconds);
        Assert.Equal("5:05", row.TimeLabel);
        Assert.Equal("Death moved 5:00 to 5:05", row.Summary);
        Assert.Equal("Clock was off", row.Reason);
        Assert.True(row.CanRevert);
        Assert.Equal(r.AppliedEventId, row.AppliedEventId);
    }

    [Fact]
    public async Task VodSnapshot_DecoratesFixedAddedAndFightPins()
    {
        using var scope = new SidecarWriteScope();
        var (events, ledger) = await SeedAsync(scope, Ev("KILL", 200, "{\"victim\":\"Zed\"}"), Ev("DEATH", 300, LeeDeath));
        await events.AppendEventsAsync(GameId, [Fight(595, 614)]);
        var deathRow = await RowAsync(events, "DEATH", 300);
        var killRow = await RowAsync(events, "KILL", 200);
        var fightRow = await RowAsync(events, "TEAMFIGHT", 595);

        var attr = await ledger.SaveAsync(Req(CorrectionOps.Attr, Subject(deathRow), Attr("fog_death", JsonValue.Create(true)), "Lee was in the fog"));
        var fightFix = await ledger.SaveAsync(Req(CorrectionOps.Attr, Subject(fightRow), Attr("verdict", JsonValue.Create("up"))));
        var added = await ledger.SaveAsync(Req(CorrectionOps.Add, null, new EventPatch("KILL", 700, null, null), "Missed by the feed"));
        var confirm = await ledger.SaveAsync(Req(CorrectionOps.Confirm, Subject(killRow)));

        var vod = await Vod(scope, events, ledger).BuildAsync(GameId);

        var death = Assert.Single(vod.GameEvents, e => e.EventType == "DEATH");
        Assert.Equal(deathRow.Id, death.Id);
        Assert.True(death.Corrected);
        Assert.False(death.AddedByUser);
        Assert.False(death.Confirmed);
        Assert.Equal(CorrectionOps.Attr, death.CorrectionOp);
        Assert.Equal(CorrectionStates.Active, death.CorrectionState);
        Assert.Equal(attr.CorrectionId, death.CorrectionId);
        Assert.Equal("det:DEATH:300:Lee Sin", death.EventKey);

        var kill = Assert.Single(vod.GameEvents, e => e.EventType == "KILL" && e.GameTimeSeconds == 200);
        Assert.True(kill.Confirmed);
        Assert.True(kill.Corrected);
        Assert.Equal(CorrectionOps.Confirm, kill.CorrectionOp);
        Assert.Equal(confirm.CorrectionId, kill.CorrectionId);

        var userKill = Assert.Single(vod.GameEvents, e => e.EventType == "KILL" && e.GameTimeSeconds == 700);
        Assert.Equal(added.AppliedEventId, userKill.Id);
        Assert.True(userKill.AddedByUser);
        Assert.False(userKill.Corrected);
        Assert.Equal(EventIdentity.UserKey(added.CorrectionId), userKill.EventKey);
        Assert.Equal(CorrectionOps.Add, userKill.CorrectionOp);

        // The stored fight pin is decorated through its stored row and reads the patched verdict.
        var pin = Assert.Single(vod.GameEvents, e => e.EventType == "TEAMFIGHT");
        Assert.Equal(fightRow.Id, pin.Id);
        Assert.True(pin.Corrected);
        Assert.Equal(CorrectionOps.Attr, pin.CorrectionOp);
        Assert.Equal(fightFix.CorrectionId, pin.CorrectionId);
        Assert.Equal("det:TEAMFIGHT:595:", pin.EventKey);
        Assert.Equal("up", pin.Teamfight!.Verdict);

        // The list is newest first and carries every op.
        Assert.Equal(4, vod.Corrections!.Count);
        Assert.Equal(CorrectionOps.Confirm, vod.Corrections[0].Op);
        Assert.Equal("Kill at 3:20 confirmed", vod.Corrections[0].Summary);
        Assert.Contains(vod.Corrections, c => c.Summary == "Kill added at 11:40");
        Assert.Contains(vod.Corrections, c => c.Summary == "Death at 5:00: fog_death = true");
        Assert.Contains(vod.Corrections, c => c.Summary == "Teamfight at 9:55: verdict = up");
        Assert.All(vod.Corrections, c => Assert.True(c.CanRevert));
    }

    [Fact]
    public async Task VodSnapshot_ReconstructsGhostsForActiveRemoves()
    {
        using var scope = new SidecarWriteScope();
        var (events, ledger) = await SeedAsync(scope,
            Ev("KILL", 200, "{\"victim\":\"Zed\"}"), Ev("DEATH", 300, LeeDeath), Ev("DEATH", 400, "{\"killer\":\"Zed\"}"));

        var r = await ledger.SaveAsync(Req(CorrectionOps.Remove, Subject(await RowAsync(events, "DEATH", 300)), reason: "Not a death"));

        var vod = await Vod(scope, events, ledger).BuildAsync(GameId);
        Assert.DoesNotContain(vod.GameEvents, e => e.EventType == "DEATH" && e.GameTimeSeconds == 300 && !e.Removed);
        var ghost = Assert.Single(vod.GameEvents, e => e.Removed);
        Assert.Equal(0, ghost.Id);
        Assert.Equal("DEATH", ghost.EventType);
        Assert.Equal(300, ghost.GameTimeSeconds);
        Assert.Equal("5:00", ghost.TimeLabel);
        Assert.Equal("DTH", ghost.ShortLabel);
        Assert.Equal("Death", ghost.Label);
        Assert.Equal("removed by you", ghost.Summary);
        Assert.Equal("loss", ghost.Kind);
        Assert.Equal("det:DEATH:300:Lee Sin", ghost.EventKey);
        Assert.Equal(CorrectionOps.Remove, ghost.CorrectionOp);
        Assert.Equal(CorrectionStates.Active, ghost.CorrectionState);
        Assert.Equal(r.CorrectionId, ghost.CorrectionId);
        // Ghosts are appended after the live markers and the fight pins.
        Assert.Same(ghost, vod.GameEvents[^1]);
        var row = Assert.Single(vod.Corrections!);
        Assert.Equal("Death at 5:00 removed", row.Summary);
        Assert.True(row.CanRevert);

        // Reverting the removal brings the row (and its key) back and drops the ghost.
        await ledger.RevertAsync(GameId, r.CorrectionId);
        vod = await Vod(scope, events, ledger).BuildAsync(GameId);
        Assert.DoesNotContain(vod.GameEvents, e => e.Removed);
        var restored = Assert.Single(vod.GameEvents, e => e.EventType == "DEATH" && e.GameTimeSeconds == 300);
        Assert.True(restored.Id > 0);
        Assert.Equal("det:DEATH:300:Lee Sin", restored.EventKey);
        Assert.False(restored.Corrected);
        Assert.Equal("", restored.CorrectionState);
        row = Assert.Single(vod.Corrections!);
        Assert.Equal(CorrectionStates.Reverted, row.State);
        Assert.Equal("reverted", row.StateLabel);
        Assert.False(row.CanRevert);
    }

    [Fact]
    public async Task VodSnapshot_WithoutRepository_StillBuilds()
    {
        using var scope = new SidecarWriteScope();
        var (events, ledger) = await SeedAsync(scope, Ev("KILL", 200, "{\"victim\":\"Zed\"}"), Ev("DEATH", 300, LeeDeath));
        await ledger.SaveAsync(Req(CorrectionOps.Retime, Subject(await RowAsync(events, "DEATH", 300)), new EventPatch(null, 305, null, null)));

        // The seven-argument ctor (pre-ledger call sites) still builds a complete snapshot.
        var vod = await new VodSnapshotBuilder(scope.Games, scope.Vod, events, scope.Evidence, scope.Objectives, scope.Config,
            NullLogger<VodSnapshotBuilder>.Instance).BuildAsync(GameId);

        Assert.NotNull(vod.EventTypeCatalog);
        Assert.NotEmpty(vod.EventTypeCatalog!);
        Assert.NotNull(vod.Corrections);
        Assert.Empty(vod.Corrections!);
        var death = Assert.Single(vod.GameEvents, e => e.EventType == "DEATH");
        Assert.Equal(305, death.GameTimeSeconds);
        Assert.Equal("det:DEATH:300:Lee Sin", death.EventKey);
        Assert.False(death.Corrected);
        Assert.DoesNotContain(vod.GameEvents, e => e.Removed);
    }

    [Fact]
    public async Task ReviewSnapshot_DeathsCarryEventKeyAndId_HeaderCountsTimelineFixes()
    {
        using var scope = new SidecarWriteScope();
        var (events, ledger) = await SeedAsync(scope,
            Ev("KILL", 200, "{\"victim\":\"Zed\"}"), Ev("DEATH", 300, LeeDeath), Ev("DEATH", 400, "{\"killer\":\"Zed\"}"));
        var first = await RowAsync(events, "DEATH", 300);
        var second = await RowAsync(events, "DEATH", 400);

        var review = await Review(scope, events, ledger).BuildAsync(GameId);
        Assert.NotNull(review.Subject);
        Assert.Equal(0, review.Subject!.Header.TimelineFixes);
        Assert.Equal(2, review.Subject.Deaths.Count);
        Assert.Equal(first.Id, review.Subject.Deaths[0].EventId);
        Assert.Equal("det:DEATH:300:Lee Sin", review.Subject.Deaths[0].EventKey);
        Assert.Equal(second.Id, review.Subject.Deaths[1].EventId);
        Assert.Equal("det:DEATH:400:Zed", review.Subject.Deaths[1].EventKey);

        // "wrong time" on one death, "not a death" on the other.
        await ledger.SaveAsync(Req(CorrectionOps.Retime, Subject(second), new EventPatch(null, 420, null, null), "Wrong time"));
        await ledger.SaveAsync(Req(CorrectionOps.Remove, Subject(first), reason: "Not a death"));

        review = await Review(scope, events, ledger).BuildAsync(GameId);
        Assert.Equal(2, review.Subject!.Header.TimelineFixes);
        var death = Assert.Single(review.Subject.Deaths);
        Assert.Equal(420, death.GameTimeSeconds);
        Assert.Equal("07:00", death.TimeText);
        Assert.Equal(second.Id, death.EventId);
        Assert.Equal("det:DEATH:400:Zed", death.EventKey);

        // Without a ledger the count is 0 and the rows still carry their identity.
        var plain = await Review(scope, events, null).BuildAsync(GameId);
        Assert.Equal(0, plain.Subject!.Header.TimelineFixes);
        Assert.Equal(second.Id, Assert.Single(plain.Subject.Deaths).EventId);
    }
}
