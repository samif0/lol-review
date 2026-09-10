using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed partial class EventCorrectionsRepositoryTests
{
    private const long GameId = 7001;

    private static GameEvent Ev(string type, int t, string details = "{}") =>
        new() { GameId = GameId, EventType = type, GameTimeS = t, Details = details };

    private static Dictionary<string, JsonNode?> Attrs(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in pairs) d[k] = JsonValue.Create(v);
        return d;
    }

    private static EventCorrectionSubject Subject(GameEvent row) => new(row.EventKey, row.Id, row.EventType, row.GameTimeS);

    private static EventCorrectionRequest Req(string op, EventCorrectionSubject? subject, EventPatch? patch = null,
        string reason = "", string? cid = null, string? note = null) =>
        new(GameId, cid ?? Guid.NewGuid().ToString("D"), op, subject, patch ?? EventPatch.Empty, reason, "3.11.0", note);

    private static async Task<(EventCorrectionsRepository Repo, List<GameEvent> Rows)> SeedAsync(TestDatabaseScope scope, params GameEvent[] events)
    {
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(GameId));
        if (events.Length > 0) await scope.GameEvents.SaveEventsAsync(GameId, events);
        return (new EventCorrectionsRepository(scope.ConnectionFactory), await RowsAsync(scope));
    }

    private static async Task<List<GameEvent>> RowsAsync(TestDatabaseScope scope, long gameId = GameId)
    {
        using var conn = scope.OpenConnection();
        return await EventCorrectionSql.LoadRowsAsync(conn, null, gameId);
    }

    private static async Task<object?> Scalar(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static JsonObject Details(GameEvent row) => (JsonObject)JsonNode.Parse(row.Details)!;

    [Fact]
    public async Task Retype_UpdatesInPlace_KeepsId_WritesMarkerAndKey()
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope, Ev("KILL", 500, "{\"victim\":\"Jinx\"}"), Ev("DEATH", 600, "{\"killer\":\"Ahri\"}"));
        // Rows that predate the ledger (v3.11 inserts stamp event_key; these are the unstamped kind).
        await Scalar(scope, "UPDATE game_events SET event_key = NULL");
        var rows = await RowsAsync(scope);
        var kill = rows.Single(r => r.EventType == "KILL");
        Assert.Null(kill.EventKey);

        var r = await repo.SaveAsync(Req(CorrectionOps.Retype, Subject(kill), new EventPatch("ASSIST", null, null, null), "It was an assist"));

        Assert.False(r.Idempotent);
        Assert.Equal(CorrectionStates.Active, r.State);
        Assert.Equal("det:KILL:500:Jinx", r.EventKey);
        Assert.Equal(kill.Id, r.AppliedEventId);
        Assert.Equal("", r.Message);

        var after = await RowsAsync(scope);
        var row = Assert.Single(after, x => x.Id == kill.Id);
        Assert.Equal("ASSIST", row.EventType);
        Assert.Equal(500, row.GameTimeS);
        Assert.Equal("det:KILL:500:Jinx", row.EventKey);
        Assert.Equal("Jinx", Details(row)["victim"]!.GetValue<string>());
        Assert.Equal((r.CorrectionId, "retype"), EventPatching.ReadMarker(row.Details));
        // The untouched neighbour stays unstamped: only the subject gets its key.
        Assert.Null(Assert.Single(after, x => x.EventType == "DEATH").EventKey);

        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal("KILL", c.SubjectType);
        Assert.Equal(500, c.SubjectTimeS);
        Assert.Equal("KILL", c.Original.EventType);
        Assert.Equal("ASSIST", c.Patch.EventType);
        Assert.Equal(CorrectionDetectors.Live, c.Detector);
        Assert.Null(c.DeltaS);
        Assert.Equal("3.11.0", c.AppVersion);
        Assert.Equal("It was an assist", c.Reason);
        Assert.Equal(CorrectionShareStates.Held, c.ShareState);
    }

    [Fact]
    public async Task Retime_MovesRowAndDeathClassification()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"));
        var deaths = new DeathClassificationsRepository(scope.ConnectionFactory);
        await deaths.UpsertAsync(GameId, 812, DeathClasses.Vision);

        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));

        Assert.Equal("", r.Message);
        var row = Assert.Single(await RowsAsync(scope));
        Assert.Equal(815, row.GameTimeS);
        var classes = await deaths.GetForGameAsync(GameId);
        var moved = Assert.Single(classes);
        Assert.Equal(815, moved.GameTimeSeconds);
        Assert.Equal(DeathClasses.Vision, moved.DeathClass);
        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal(3, c.DeltaS);
        Assert.Equal(812, c.SubjectTimeS);
        Assert.Equal(815, c.EffectiveTimeS);
    }

    [Fact]
    public async Task Retime_DeathClassificationCollision_ExistingWins_ReturnsMessage()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"), Ev("DEATH", 815, "{\"killer\":\"Ahri\"}"));
        var deaths = new DeathClassificationsRepository(scope.ConnectionFactory);
        await deaths.UpsertAsync(GameId, 812, DeathClasses.Greed);
        await deaths.UpsertAsync(GameId, 815, DeathClasses.Vision);

        var r = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));

        Assert.Equal(EventCorrectionsRepository.CauseResetMessage, r.Message);
        var kept = Assert.Single(await deaths.GetForGameAsync(GameId));
        Assert.Equal(815, kept.GameTimeSeconds);
        Assert.Equal(DeathClasses.Vision, kept.DeathClass);
        Assert.Equal(2, (await RowsAsync(scope)).Count(x => x.GameTimeS == 815));
    }

    [Fact]
    public async Task Attr_MergesAttrs_PreservesOtherKeys()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope,
            Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"assisters\":[\"Ahri\"],\"map_state\":true,\"fog_death\":true,\"enemy_jg_dark_s\":74}"));

        var r = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(rows[0]), new EventPatch(null, null, null, Attrs(("fog_death", false)))));

        var d = Details(Assert.Single(await RowsAsync(scope)));
        Assert.False(d["fog_death"]!.GetValue<bool>());
        Assert.Equal("Lee Sin", d["killer"]!.GetValue<string>());
        Assert.Equal(74, d["enemy_jg_dark_s"]!.GetValue<int>());
        Assert.True(d["map_state"]!.GetValue<bool>());
        Assert.Equal("fog_death", Assert.Single((JsonArray)d["correction"]!["attrs"]!)!.GetValue<string>());
        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal(CorrectionDetectors.MapState, c.Detector);
        Assert.Contains("\"fog_death\":true", c.Original.Details);
        Assert.Equal(r.Id, c.Id);
    }

    [Fact]
    public async Task Remove_DeletesRowAndItsClassification()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"), Ev("KILL", 900));
        var deaths = new DeathClassificationsRepository(scope.ConnectionFactory);
        await deaths.UpsertAsync(GameId, 812, DeathClasses.Tempo);
        var death = rows.Single(x => x.EventType == "DEATH");

        var r = await repo.SaveAsync(Req(CorrectionOps.Remove, Subject(death), reason: "Not a death"));

        Assert.Null(r.AppliedEventId);
        Assert.Equal("det:DEATH:812:Lee Sin", r.EventKey);
        Assert.Single(await RowsAsync(scope), x => x.EventType == "KILL");
        Assert.Empty(await deaths.GetForGameAsync(GameId));
        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal(CorrectionOps.Remove, c.Op);
        Assert.True(c.Patch.IsEmpty);
        Assert.Equal("DEATH", c.Original.EventType);

        // Anything else on that subject must revert the removal first.
        var again = new EventCorrectionSubject(r.EventKey, death.Id, "DEATH", 812);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.SaveAsync(Req(CorrectionOps.Retime, again, new EventPatch(null, 815, null, null))));
        Assert.Equal(EventCorrectionsRepository.EventRemovedMessage, ex.Message);
    }

    [Fact]
    public async Task Add_InsertsUserKeyedRow()
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope, Ev("KILL", 900));

        var r = await repo.SaveAsync(Req(CorrectionOps.Add, null, new EventPatch("DRAGON", 1200, null, null), "Missed by the feed"));

        Assert.Equal("usr:" + r.CorrectionId, r.EventKey);
        Assert.NotNull(r.AppliedEventId);
        var row = Assert.Single(await RowsAsync(scope), x => x.EventType == "DRAGON");
        Assert.Equal(r.AppliedEventId, row.Id);
        Assert.Equal(1200, row.GameTimeS);
        Assert.Equal(r.EventKey, row.EventKey);
        Assert.Equal((r.CorrectionId, "add"), EventPatching.ReadMarker(row.Details));
        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal("DRAGON", c.SubjectType);
        Assert.Equal(1200, c.SubjectTimeS);
        Assert.True(c.Original.IsNone);
        Assert.Equal(CorrectionDetectors.Manual, c.Detector);
        Assert.Null(c.DeltaS);
    }

    [Fact]
    public async Task Confirm_SetsConfirmedMarker()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"fog_death\":true}"));

        var r = await repo.SaveAsync(Req(CorrectionOps.Confirm, Subject(rows[0])));

        var d = Details(Assert.Single(await RowsAsync(scope)));
        Assert.True(d["correction"]!["confirmed"]!.GetValue<bool>());
        Assert.True(d["fog_death"]!.GetValue<bool>());
        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.True(c.Patch.Confirmed);
        Assert.Equal(r.Id, c.Id);

        // A later fix clears the confirmation.
        var row = Assert.Single(await RowsAsync(scope));
        await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(row), new EventPatch(null, null, null, Attrs(("fog_death", false)))));
        var after = Details(Assert.Single(await RowsAsync(scope)));
        Assert.Null(after["correction"]!["confirmed"]);
        Assert.False(Assert.Single(await repo.GetActiveForGameAsync(GameId)).Patch.Confirmed);
    }

    [Fact]
    public async Task Save_IsIdempotentOnCorrectionId_InAnyState()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"));
        var first = Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null));
        var r1 = await repo.SaveAsync(first);
        var again = await repo.SaveAsync(first);
        Assert.True(again.Idempotent);
        Assert.Equal(r1.Id, again.Id);
        Assert.Equal(CorrectionStates.Active, again.State);

        var moved = Assert.Single(await RowsAsync(scope));
        var second = Req(CorrectionOps.Retime, Subject(moved), new EventPatch(null, 818, null, null));
        var r2 = await repo.SaveAsync(second);
        var superseded = await repo.SaveAsync(first);
        Assert.True(superseded.Idempotent);
        Assert.Equal(CorrectionStates.Superseded, superseded.State);
        Assert.Equal(r1.Id, superseded.Id);

        await repo.RevertAsync(GameId, r2.CorrectionId);
        var reverted = await repo.SaveAsync(second);
        Assert.True(reverted.Idempotent);
        Assert.Equal(CorrectionStates.Reverted, reverted.State);
        Assert.Equal(1L, await Scalar(scope, "SELECT COUNT(*) FROM event_corrections WHERE op <> 'revert' AND correction_id = '" + r2.CorrectionId + "'"));
        Assert.Equal(812, Assert.Single(await RowsAsync(scope)).GameTimeS);
    }

    [Fact]
    public async Task Save_SupersedesPriorRow_WithCumulativePatch()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"fog_death\":true}"));
        var r1 = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));
        var moved = Assert.Single(await RowsAsync(scope));
        var r2 = await repo.SaveAsync(Req(CorrectionOps.Attr, Subject(moved), new EventPatch(null, null, null, Attrs(("fog_death", false)))));

        var active = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal(r2.Id, active.Id);
        Assert.Equal(815, active.Patch.GameTimeS);
        Assert.False(active.Patch.Attrs!["fog_death"]!.GetValue<bool>());
        Assert.Equal(r1.Id, active.SupersedesId);
        Assert.Equal("DEATH", active.Original.EventType);
        Assert.Equal(812, active.Original.GameTimeS);
        Assert.Equal(3, active.DeltaS);

        var all = await repo.GetForGameAsync(GameId);
        Assert.Equal(2, all.Count);
        Assert.Equal(CorrectionStates.Superseded, all.Single(c => c.Id == r1.Id).State);
        var row = Assert.Single(await RowsAsync(scope));
        Assert.Equal(815, row.GameTimeS);
        Assert.False(Details(row)["fog_death"]!.GetValue<bool>());
        Assert.Equal((r2.CorrectionId, "attr"), EventPatching.ReadMarker(row.Details));
    }

    [Theory]
    [InlineData("game", EventCorrectionsRepository.GameMissingMessage)]
    [InlineData("op", EventCorrectionsRepository.OpMessage)]
    [InlineData("cid", EventCorrectionsRepository.CorrectionIdMessage)]
    [InlineData("subject_missing", EventCorrectionsRepository.EventMissingMessage)]
    [InlineData("subject_not_found", EventCorrectionsRepository.EventMissingMessage)]
    [InlineData("subject_changed", EventCorrectionsRepository.EventChangedMessage)]
    [InlineData("synthetic", EventCorrectionsRepository.SyntheticFightMessage)]
    [InlineData("unknown_type", EventCorrectionsRepository.TypeMessage)]
    [InlineData("time_negative", EventCorrectionsRepository.TimeMessage)]
    [InlineData("end_before_start", EventCorrectionsRepository.TimeMessage)]
    [InlineData("beyond_duration", EventCorrectionsRepository.TimeMessage)]
    [InlineData("attr_key", EventCorrectionCatalog.AttrNotEditableMessage)]
    [InlineData("attr_value", EventCorrectionCatalog.AttrValueMessage)]
    [InlineData("reason", EventCorrectionsRepository.ReasonMessage)]
    [InlineData("retype_same", EventCorrectionsRepository.NothingToChangeMessage)]
    [InlineData("retime_same", EventCorrectionsRepository.NothingToChangeMessage)]
    [InlineData("attr_same", EventCorrectionsRepository.NothingToChangeMessage)]
    [InlineData("attr_empty", EventCorrectionsRepository.NothingToChangeMessage)]
    public async Task Save_RejectsNothingToChange_AndEachValidationMessage(string when, string expected)
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"fog_death\":true}"));
        var death = rows[0];
        var s = Subject(death);
        EventCorrectionRequest request = when switch
        {
            "game" => Req(CorrectionOps.Retime, s, new EventPatch(null, 815, null, null)) with { GameId = 999_999 },
            "op" => Req("nuke", s),
            "cid" => Req(CorrectionOps.Retime, s, new EventPatch(null, 815, null, null), cid: "not-a-guid"),
            "subject_missing" => Req(CorrectionOps.Retime, null, new EventPatch(null, 815, null, null)),
            "subject_not_found" => Req(CorrectionOps.Retime, new EventCorrectionSubject(null, 999_999, "DEATH", 812), new EventPatch(null, 815, null, null)),
            "subject_changed" => Req(CorrectionOps.Retime, new EventCorrectionSubject(null, death.Id, "DEATH", 700), new EventPatch(null, 815, null, null)),
            "synthetic" => Req(CorrectionOps.Retime, new EventCorrectionSubject(null, -3, "TEAMFIGHT", 600), new EventPatch(null, 610, null, null)),
            "unknown_type" => Req(CorrectionOps.Retype, s, new EventPatch("FLASH", null, null, null)),
            "time_negative" => Req(CorrectionOps.Retime, s, new EventPatch(null, -5, null, null)),
            "end_before_start" => Req(CorrectionOps.Add, null, new EventPatch("TRADE", 100, 90, null)),
            "beyond_duration" => Req(CorrectionOps.Retime, s, new EventPatch(null, 5000, null, null)),
            "attr_key" => Req(CorrectionOps.Attr, s, new EventPatch(null, null, null, Attrs(("who", "enemy")))),
            "attr_value" => Req(CorrectionOps.Attr, s, new EventPatch(null, null, null, Attrs(("fog_death", "yes")))),
            "reason" => Req(CorrectionOps.Retime, s, new EventPatch(null, 815, null, null), new string('x', 281)),
            "retype_same" => Req(CorrectionOps.Retype, s, new EventPatch("DEATH", null, null, null)),
            "retime_same" => Req(CorrectionOps.Retime, s, new EventPatch(null, 812, null, null)),
            "attr_same" => Req(CorrectionOps.Attr, s, new EventPatch(null, null, null, Attrs(("fog_death", true)))),
            "attr_empty" => Req(CorrectionOps.Attr, s, EventPatch.Empty),
            _ => throw new InvalidOperationException(when),
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(request));
        Assert.Equal(expected, ex.Message);
        Assert.Equal(0L, await Scalar(scope, "SELECT COUNT(*) FROM event_corrections"));
        var untouched = Assert.Single(await RowsAsync(scope));
        Assert.Equal(death.Details, untouched.Details);
        Assert.Equal(812, untouched.GameTimeS);
    }

    [Fact]
    public async Task Save_EncounterTypes_EmitLegacyDetailsKeys()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("TRADE", 110, "{\"detected\":true,\"kind\":\"extended\",\"hp_lost_pct\":31}"));

        var r = await repo.SaveAsync(Req(CorrectionOps.Retype, Subject(rows[0]), new EventPatch("ALL_IN", 100, 114, null),
            reason: "Committed pursuit", note: "Committed pursuit"));

        var row = Assert.Single(await RowsAsync(scope));
        var d = Details(row);
        Assert.Equal("ALL_IN", row.EventType);
        Assert.Equal(100, row.GameTimeS);
        Assert.Equal("det:TRADE:110:", row.EventKey);
        Assert.Equal(ReviewedEncountersRepository.Source, d["source"]!.GetValue<string>());
        Assert.True(d["reviewed"]!.GetValue<bool>());
        Assert.Equal(1, d["classification_version"]!.GetValue<int>());
        Assert.Equal(r.CorrectionId, d["request_id"]!.GetValue<string>());
        Assert.Equal("all_in", d["classification"]!.GetValue<string>());
        Assert.Null(d["kind"]);
        Assert.Equal(100, d["start_s"]!.GetValue<int>());
        Assert.Equal(114, d["end_s"]!.GetValue<int>());
        Assert.Equal(14, d["duration_s"]!.GetValue<int>());
        Assert.Equal("Committed pursuit", d["note"]!.GetValue<string>());
        Assert.Equal("TRADE", d["original_type"]!.GetValue<string>());
        Assert.Equal(110, d["original_time_s"]!.GetValue<int>());
        Assert.Contains("\"kind\":\"extended\"", d["original_details"]!.GetValue<string>());
        Assert.Equal(31, d["hp_lost_pct"]!.GetValue<int>());

        // A reviewed encounter survives the legacy capture-replacement delete exactly as before.
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("TRADE", 110, "{\"kind\":\"extended\"}"), Ev("KILL", 115)]);
        var kept = await RowsAsync(scope);
        Assert.Equal(2, kept.Count);
        Assert.Equal(row.Id, Assert.Single(kept, x => x.EventType == "ALL_IN").Id);

        // An added trade carries the classification pair; a retype away from an encounter drops the source.
        var add = await repo.SaveAsync(Req(CorrectionOps.Add, null, new EventPatch("TRADE", 191, 192, Attrs(("kind", "Short")))));
        var trade = Details(Assert.Single(await RowsAsync(scope), x => x.Id == add.AppliedEventId));
        Assert.Equal("short", trade["classification"]!.GetValue<string>());
        Assert.Equal("short", trade["kind"]!.GetValue<string>());
        Assert.Null(trade["original_type"]);
        var allIn = Assert.Single(await RowsAsync(scope), x => x.EventType == "ALL_IN");
        await repo.SaveAsync(Req(CorrectionOps.Retype, Subject(allIn), new EventPatch("KILL", null, null, null)));
        var killed = Details(Assert.Single(await RowsAsync(scope), x => x.Id == allIn.Id));
        Assert.Null(killed["source"]);
        Assert.Null(killed["reviewed"]);
    }
}
