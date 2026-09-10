using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>Rules B (post-game delete-by-type + append) and C (details stamps), the no-ledger
/// invariant and the schema-less fallback. Helpers live in the sibling partial.</summary>
public sealed partial class EventCorrectionApplierTests
{
    [Fact]
    public async Task RuleB_DeleteByTypeKeepsCorrectedTeamfight_AppendSuppressesTwinWithin10s_AbsorbsWithin1s()
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope, Ev("KILL", 600));
        await scope.GameEvents.AppendEventsAsync(GameId, [Fight(595, 614)]);
        var fight = Assert.Single(await RowsAsync(scope), x => x.EventType == "TEAMFIGHT");
        Assert.Equal("det:TEAMFIGHT:595:", fight.EventKey);
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(fight), new EventPatch(null, 600, null, null), "Fight started on the kill"));
        var corrected = Assert.Single(await RowsAsync(scope), x => x.Id == fight.Id);
        Assert.Equal(600, corrected.GameTimeS);
        Assert.Equal(600, Details(corrected)["start_s"]!.GetValue<int>());
        Assert.Equal(614, Details(corrected)["end_s"]!.GetValue<int>());

        // The map-state pass re-runs: delete keeps the corrected fight, the append's twin is suppressed.
        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "TEAMFIGHT");
        Assert.Single(await RowsAsync(scope), x => x.EventType == "TEAMFIGHT");
        await scope.GameEvents.AppendEventsAsync(GameId, [Fight(597, 614)]);
        var after = await RowsAsync(scope);
        var kept = Assert.Single(after, x => x.EventType == "TEAMFIGHT");
        Assert.Equal(fight.Id, kept.Id);
        Assert.Equal(600, Details(kept)["start_s"]!.GetValue<int>());
        Assert.Equal("det:TEAMFIGHT:597:", kept.EventKey);
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal("det:TEAMFIGHT:597:", c.SubjectKey);
        Assert.Equal("det:TEAMFIGHT:595:", c.RebasedFrom);
        Assert.Equal(fight.Id, c.AppliedEventId);

        // A detector bump that lands within a second of the fix absorbs it.
        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "TEAMFIGHT");
        await scope.GameEvents.AppendEventsAsync(GameId, [Fight(600, 614)]);
        after = await RowsAsync(scope);
        Assert.Equal(fight.Id, Assert.Single(after, x => x.EventType == "TEAMFIGHT").Id);
        Assert.Single(after, x => x.EventType == "KILL");
        c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Absorbed, c.State);
        Assert.Equal("det:TEAMFIGHT:600:", c.SubjectKey);
    }

    [Fact]
    public async Task RuleB_JungleProximityRemove_SuppressesTwin()
    {
        using var scope = new TestDatabaseScope();
        var proximity = new[] { Ev("JUNGLE_PROXIMITY", 240, "{\"who\":\"enemy\",\"champion\":\"Lee Sin\",\"detected\":true}") };
        var (repo, _) = await SeedAsync(scope, Ev("DEATH", 300, LeeDeath));
        await scope.GameEvents.AppendEventsAsync(GameId, proximity);
        var visit = Assert.Single(await RowsAsync(scope), x => x.EventType == "JUNGLE_PROXIMITY");
        Assert.Equal("det:JUNGLE_PROXIMITY:240:enemy", visit.EventKey);
        var r = await repo.SaveAsync(Req(CorrectionOps.Remove, Subject(visit), reason: "He was on his own side"));

        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "JUNGLE_PROXIMITY");
        await scope.GameEvents.AppendEventsAsync(GameId, proximity);

        var after = await RowsAsync(scope);
        Assert.Single(after);
        Assert.Equal("DEATH", after[0].EventType);
        Assert.Equal(CorrectionStates.Active, (await LedgerAsync(repo, r.CorrectionId)).State);
    }

    [Fact]
    public async Task RuleB_OutOfScopeCorrectionsUntouched()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, LeeDeath));
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));
        var before = await LedgerAsync(repo, r.CorrectionId);

        // Neither a proximity append nor a fight append re-detects deaths.
        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "JUNGLE_PROXIMITY");
        await scope.GameEvents.AppendEventsAsync(GameId, [Ev("JUNGLE_PROXIMITY", 240, "{\"who\":\"enemy\"}")]);
        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "TEAMFIGHT");
        await scope.GameEvents.AppendEventsAsync(GameId, [Fight(595, 614)]);

        var after = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(before, after);
        Assert.Equal(CorrectionStates.Active, after.State);
        var rowsAfter = await RowsAsync(scope);
        Assert.Equal(3, rowsAfter.Count);
        Assert.Equal(815, Assert.Single(rowsAfter, x => x.EventType == "DEATH").GameTimeS);
    }

    [Fact]
    public async Task RuleC_FogDeathTrueStampOverFalseCorrection_StaysFalse_KeepsOtherStamps()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"map_state\":true,\"fog_death\":true,\"enemy_jg_dark_s\":40}"));
        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("fog_death", false))), "He was pinged"));

        // The analyzer re-stamps from the stored row (it only ever writes fog_death=true).
        var stored = Assert.Single(await scope.GameEvents.GetEventsAsync(GameId));
        var stamped = Details(stored);
        stamped["fog_death"] = true;
        stamped["enemy_jg_dark_s"] = 74;
        stamped["ally_jg_dist"] = 900;
        await scope.GameEvents.UpdateEventDetailsAsync(stored.Id, stamped.ToJsonString());

        var d = Details(Assert.Single(await RowsAsync(scope)));
        Assert.False(d["fog_death"]!.GetValue<bool>());
        Assert.Equal(74, d["enemy_jg_dark_s"]!.GetValue<int>());
        Assert.Equal(900, d["ally_jg_dist"]!.GetValue<int>());
        Assert.True(d["map_state"]!.GetValue<bool>());
        Assert.Equal("Lee Sin", d["killer"]!.GetValue<string>());
        Assert.Equal((r.CorrectionId, "attr"), EventPatching.ReadMarker(d.ToJsonString()));
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(stored.Id, c.AppliedEventId);
    }

    [Fact]
    public async Task RuleC_StampAgreeingWithCorrection_FlipsAbsorbed()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, LeeDeath));
        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("fog_death", true))), "Never saw him"));

        var stored = Assert.Single(await scope.GameEvents.GetEventsAsync(GameId));
        var stamped = Details(stored);
        stamped["map_state"] = true;
        stamped["fog_death"] = true;
        stamped["enemy_jg_dark_s"] = 80;
        await scope.GameEvents.UpdateEventDetailsAsync(stored.Id, stamped.ToJsonString());

        var d = Details(Assert.Single(await RowsAsync(scope)));
        Assert.True(d["fog_death"]!.GetValue<bool>());
        Assert.Equal(80, d["enemy_jg_dark_s"]!.GetValue<int>());
        Assert.True(EventPatching.HasMarker(d.ToJsonString()));
        Assert.Equal(CorrectionStates.Absorbed, (await LedgerAsync(repo, r.CorrectionId)).State);
        Assert.Single(await repo.GetActiveForGameAsync(GameId));
    }

    [Fact]
    public async Task RuleC_StampLeavingCorrectedTrueUntouched_StaysActive()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"map_state\":true,\"enemy_jg_dark_s\":12}"));
        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("fog_death", true))), "Never saw him"));

        // The analyzer re-parses the corrected row, drops the correction's own attrs first and
        // stamps only what it decided: still no fog on this death.
        var stored = Assert.Single(await scope.GameEvents.GetEventsAsync(GameId));
        var stamped = Details(stored);
        foreach (var key in EventPatching.CorrectedAttrKeys(stamped)) stamped.Remove(key);
        Assert.False(stamped.ContainsKey("fog_death"));
        stamped["map_state"] = true;
        stamped["enemy_jg_dark_s"] = 74;
        stamped["ally_jg_dist"] = 900;
        await scope.GameEvents.UpdateEventDetailsAsync(stored.Id, stamped.ToJsonString());

        var d = Details(Assert.Single(await RowsAsync(scope)));
        Assert.True(d["fog_death"]!.GetValue<bool>());
        Assert.Equal(74, d["enemy_jg_dark_s"]!.GetValue<int>());
        Assert.Equal(900, d["ally_jg_dist"]!.GetValue<int>());
        Assert.Equal((r.CorrectionId, "attr"), EventPatching.ReadMarker(d.ToJsonString()));
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(stored.Id, c.AppliedEventId);
    }

    [Fact]
    public async Task RuleC_KeyTheStampNeverWrites_LeavesRuleAVerdictAlone()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, LeeDeath));
        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("jungle_gank", true))), "He came from river"));

        // The map-state pass never decides jungle_gank: a fix the capture-time classifier
        // missed is not reported as reproduced.
        var stored = Assert.Single(await scope.GameEvents.GetEventsAsync(GameId));
        var stamped = Details(stored);
        foreach (var key in EventPatching.CorrectedAttrKeys(stamped)) stamped.Remove(key);
        stamped["map_state"] = true;
        await scope.GameEvents.UpdateEventDetailsAsync(stored.Id, stamped.ToJsonString());
        Assert.True(Details(Assert.Single(await RowsAsync(scope)))["jungle_gank"]!.GetValue<bool>());
        Assert.Equal(CorrectionStates.Active, (await LedgerAsync(repo, r.CorrectionId)).State);

        // A re-capture whose classifier now stamps the gank absorbs it (rule A)...
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"jungle_gank\":true}")]);
        Assert.Equal(CorrectionStates.Absorbed, (await LedgerAsync(repo, r.CorrectionId)).State);

        // ...and the map-state pass that follows every capture leaves that verdict alone.
        stored = Assert.Single(await scope.GameEvents.GetEventsAsync(GameId));
        stamped = Details(stored);
        foreach (var key in EventPatching.CorrectedAttrKeys(stamped)) stamped.Remove(key);
        stamped["map_state"] = true;
        stamped["enemy_jg_dark_s"] = 40;
        await scope.GameEvents.UpdateEventDetailsAsync(stored.Id, stamped.ToJsonString());
        var d = Details(Assert.Single(await RowsAsync(scope)));
        Assert.True(d["jungle_gank"]!.GetValue<bool>());
        Assert.Equal(40, d["enemy_jg_dark_s"]!.GetValue<int>());
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Absorbed, c.State);
        Assert.Equal(stored.Id, c.AppliedEventId);
    }

    [Fact]
    public async Task RuleC_StampOnAbsorbedRetime_KeepsAbsorbed()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, LeeDeath));
        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 815, LeeDeath)]);
        Assert.Equal(CorrectionStates.Absorbed, (await LedgerAsync(repo, r.CorrectionId)).State);

        // The map-state pass re-queued by that capture stamps the survivor.
        var stored = Assert.Single(await scope.GameEvents.GetEventsAsync(GameId));
        var stamped = Details(stored);
        stamped["map_state"] = true;
        await scope.GameEvents.UpdateEventDetailsAsync(stored.Id, stamped.ToJsonString());

        var kept = Assert.Single(await RowsAsync(scope));
        Assert.Equal(rows[0].Id, kept.Id);
        Assert.Equal(815, kept.GameTimeS);
        Assert.True(Details(kept)["map_state"]!.GetValue<bool>());
        Assert.Equal((r.CorrectionId, "retime"), EventPatching.ReadMarker(kept.Details));
        var c = await LedgerAsync(repo, r.CorrectionId);
        Assert.Equal(CorrectionStates.Absorbed, c.State);
        Assert.Equal(kept.Id, c.AppliedEventId);
    }

    [Fact]
    public async Task NoCorrections_EverythingBehavesAsBefore_AndKeysAreStamped()
    {
        using var scope = new TestDatabaseScope();
        var captured = new[]
        {
            Ev("KILL", 500, "{\"victim\":\"Jinx\"}"),
            Ev("DEATH", 600, "{\"killer\":\"Ahri\"}"),
            Ev("DEATH", 600, "{\"killer\":\"Ahri\"}"),
            Ev("TRADE", 700, "{\"detected\":true,\"kind\":\"short\"}"),
        };
        var (_, rows) = await SeedAsync(scope, captured);
        Assert.Equal(
            new[] { "det:KILL:500:Jinx", "det:DEATH:600:Ahri", "det:DEATH:600:Ahri#2", "det:TRADE:700:" },
            rows.Select(x => x.EventKey));
        var readBack = await scope.GameEvents.GetEventsAsync(GameId);
        Assert.Equal(rows.Select(x => x.EventKey), readBack.Select(x => x.EventKey));

        await scope.GameEvents.AppendEventsAsync(GameId, [Ev("JUNGLE_PROXIMITY", 240, "{\"who\":\"enemy\"}")]);
        var visit = Assert.Single(await RowsAsync(scope), x => x.EventType == "JUNGLE_PROXIMITY");
        Assert.Equal("det:JUNGLE_PROXIMITY:240:enemy", visit.EventKey);
        await scope.GameEvents.DeleteEventsByTypeAsync(GameId, "JUNGLE_PROXIMITY");
        Assert.DoesNotContain(await RowsAsync(scope), x => x.EventType == "JUNGLE_PROXIMITY");

        var death = rows.First(x => x.EventType == "DEATH");
        await scope.GameEvents.UpdateEventDetailsAsync(death.Id, "{\"map_state\":true}");
        Assert.Equal("{\"map_state\":true}", Assert.Single(await RowsAsync(scope), x => x.Id == death.Id).Details);

        // A re-save replaces every row (fresh ids), exactly as before the ledger.
        await scope.GameEvents.SaveEventsAsync(GameId, captured);
        var replaced = await RowsAsync(scope);
        Assert.Equal(4, replaced.Count);
        Assert.Empty(replaced.Select(x => x.Id).Intersect(rows.Select(x => x.Id)));
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event_corrections";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Planner_SchemaLessDatabase_FallsBackToPassThrough()
    {
        using var db = new LedgerlessDatabase();
        var events = new GameEventsRepository(db);
        var incoming = new[] { Ev("KILL", 100, "{\"victim\":\"Jinx\"}"), Ev("DEATH", 200, LeeDeath) };

        using (var conn = db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            var keys = EventIdentity.KeyForBatch(incoming);
            var plan = await EventCorrectionApplier.PlanIncomingAsync(conn, tx, GameId, incoming, keys, EventCorrectionApplier.LiveTypes);
            Assert.All(plan.Decisions, d => Assert.Equal(IncomingAction.Insert, d.Action));
            Assert.Equal(keys, plan.Decisions.Select(d => d.EventKey));
            Assert.Empty(plan.Extras);
            Assert.Equal(0, plan.Suppressed + plan.Absorbed + plan.Orphaned + plan.Reapplied);
        }

        await events.SaveEventsAsync(GameId, incoming);
        await events.AppendEventsAsync(GameId, [Ev("JUNGLE_PROXIMITY", 240, "{\"who\":\"ally\"}")]);
        var saved = await events.GetEventsAsync(GameId);
        Assert.Equal(3, saved.Count);
        Assert.Equal("det:KILL:100:Jinx", saved[0].EventKey);
        await events.UpdateEventDetailsAsync(saved[1].Id, "{\"map_state\":true}");
        Assert.Contains("map_state", (await events.GetEventsAsync(GameId))[1].Details);
    }

    /// <summary>games + game_events (with event_key) and NO event_corrections table.</summary>
    private sealed class LedgerlessDatabase : IDbConnectionFactory, IDisposable
    {
        public string DatabasePath { get; } = $"ledgerless-{Guid.NewGuid():N}";
        private readonly SqliteConnection _keeper;

        public LedgerlessDatabase()
        {
            _keeper = CreateConnection();
            using var cmd = _keeper.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE games(game_id INTEGER PRIMARY KEY, game_duration INTEGER);
                INSERT INTO games VALUES({GameId}, 1800);
                CREATE TABLE game_events(id INTEGER PRIMARY KEY AUTOINCREMENT, game_id INTEGER,
                    event_type TEXT, game_time_s INTEGER, details TEXT, event_key TEXT);
                """;
            cmd.ExecuteNonQuery();
        }

        public SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection($"Data Source={DatabasePath};Mode=Memory;Cache=Shared");
            conn.Open();
            return conn;
        }

        public void Dispose() => _keeper.Dispose();
    }
}
