using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>Revert, the read side, export, the rule G key stamp, game deletion and the legacy
/// encounter delegation. Helpers live in the sibling partial.</summary>
public sealed partial class EventCorrectionsRepositoryTests
{
    [Theory]
    [InlineData(CorrectionOps.Retype)]
    [InlineData(CorrectionOps.Retime)]
    [InlineData(CorrectionOps.Attr)]
    [InlineData(CorrectionOps.Remove)]
    [InlineData(CorrectionOps.Add)]
    public async Task Revert_RestoresOriginal_PerOp(string op)
    {
        using var scope = new TestDatabaseScope();
        const string details = "{\"killer\":\"Lee Sin\",\"fog_death\":true}";
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, details), Ev("KILL", 900, "{\"victim\":\"Jinx\"}"));
        var deaths = new DeathClassificationsRepository(scope.ConnectionFactory);
        await deaths.UpsertAsync(GameId, 812, DeathClasses.Vision);
        var death = rows.Single(x => x.EventType == "DEATH");

        var saved = await repo.SaveAsync(op switch
        {
            CorrectionOps.Retype => Req(op, Subject(death), new EventPatch("KILL", null, null, null)),
            CorrectionOps.Retime => Req(op, Subject(death), new EventPatch(null, 815, null, null)),
            CorrectionOps.Attr => Req(op, Subject(death), new EventPatch(null, null, null, Attrs(("fog_death", false)))),
            CorrectionOps.Remove => Req(op, Subject(death)),
            _ => Req(op, null, new EventPatch("DRAGON", 1200, null, null)),
        });

        var r = await repo.RevertAsync(GameId, saved.CorrectionId, "changed my mind");

        Assert.False(r.Idempotent);
        Assert.Equal(CorrectionOps.Revert, r.Op);
        Assert.Equal(CorrectionStates.Reverted, r.State);
        Assert.Equal(saved.CorrectionId, r.CorrectionId);
        Assert.Equal(saved.EventKey, r.EventKey);

        var after = await RowsAsync(scope);
        if (op == CorrectionOps.Add)
        {
            Assert.DoesNotContain(after, x => x.EventType == "DRAGON");
            Assert.Null(r.AppliedEventId);
        }
        else
        {
            var restored = Assert.Single(after, x => x.EventType == "DEATH");
            Assert.Equal(812, restored.GameTimeS);
            Assert.Equal("det:DEATH:812:Lee Sin", restored.EventKey);
            Assert.False(EventPatching.HasMarker(restored.Details));
            Assert.True(Details(restored)["fog_death"]!.GetValue<bool>());
            Assert.Equal("Lee Sin", Details(restored)["killer"]!.GetValue<string>());
            Assert.Equal(restored.Id, r.AppliedEventId);
            if (op == CorrectionOps.Remove) Assert.NotEqual(death.Id, restored.Id);
            else Assert.Equal(death.Id, restored.Id);
            if (op != CorrectionOps.Remove)
            {
                var cls = Assert.Single(await deaths.GetForGameAsync(GameId));
                Assert.Equal(812, cls.GameTimeSeconds);
            }
        }
        Assert.Equal(2, after.Count);
        Assert.Empty(await repo.GetActiveForGameAsync(GameId));
        var all = await repo.GetForGameAsync(GameId);
        var target = Assert.Single(all);
        Assert.Equal(CorrectionStates.Reverted, target.State);
        Assert.Equal(2L, await Scalar(scope, "SELECT COUNT(*) FROM event_corrections"));
        Assert.Equal(target.Id, Convert.ToInt64(await Scalar(scope, "SELECT supersedes_id FROM event_corrections WHERE op = 'revert'")));
        Assert.Equal("changed my mind", (string?)await Scalar(scope, "SELECT reason FROM event_corrections WHERE op = 'revert'"));
    }

    [Fact]
    public async Task Revert_IsIdempotent_AndRejectsSuperseded()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"));
        var a = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));
        var first = await repo.RevertAsync(GameId, a.CorrectionId);
        Assert.False(first.Idempotent);
        var second = await repo.RevertAsync(GameId, a.CorrectionId);
        Assert.True(second.Idempotent);
        Assert.Equal(CorrectionStates.Reverted, second.State);
        Assert.Equal(1L, await Scalar(scope, "SELECT COUNT(*) FROM event_corrections WHERE op = 'revert'"));

        var row = Assert.Single(await RowsAsync(scope));
        var b = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(row), new EventPatch(null, 820, null, null)));
        row = Assert.Single(await RowsAsync(scope));
        await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(row), new EventPatch(null, 825, null, null)));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.RevertAsync(GameId, b.CorrectionId));
        Assert.Equal(EventCorrectionsRepository.CorrectionReplacedMessage, ex.Message);

        ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.RevertAsync(GameId, Guid.NewGuid().ToString("D")));
        Assert.Equal(EventCorrectionsRepository.CorrectionMissingMessage, ex.Message);
        ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.RevertAsync(GameId + 1, b.CorrectionId));
        Assert.Equal(EventCorrectionsRepository.CorrectionMissingMessage, ex.Message);
        Assert.Equal(825, Assert.Single(await RowsAsync(scope)).GameTimeS);
    }

    [Fact]
    public async Task GetForGame_NewestFirst_ExcludesRevertRows_AndCountActive_CountsApplicableOnly()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"), Ev("KILL", 900, "{\"victim\":\"Jinx\"}"), Ev("DRAGON", 1000));
        var a = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));
        var b = await repo.SaveAsync(Req(CorrectionOps.Retype, Subject(rows[1]), new EventPatch("ASSIST", null, null, null)));
        var c = await repo.SaveAsync(Req(CorrectionOps.Confirm, Subject(rows[2])));
        await repo.RevertAsync(GameId, a.CorrectionId);
        using (var conn = scope.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE event_corrections SET state = 'orphaned' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", c.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var list = await repo.GetForGameAsync(GameId);
        Assert.Equal(new[] { c.Id, b.Id, a.Id }, list.Select(x => x.Id));
        Assert.DoesNotContain(list, x => x.Op == CorrectionOps.Revert);
        Assert.Equal(2, await repo.CountActiveForGameAsync(GameId));
        Assert.Equal(new[] { b.Id, c.Id }, (await repo.GetActiveForGameAsync(GameId)).Select(x => x.Id));
        Assert.Equal(0, await repo.CountActiveForGameAsync(GameId + 1));
    }

    [Fact]
    public async Task Export_ShapeAndCount()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\",\"fog_death\":true}"), Ev("KILL", 900));
        var a = await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, Attrs(("fog_death", false))), "Lee was pinged"));
        await repo.SaveAsync(Req(CorrectionOps.Retype, Subject(rows[1]), new EventPatch("ASSIST", null, null, null)));
        await repo.RevertAsync(GameId, a.CorrectionId);

        Assert.Equal(3, (await repo.ExportItemsAsync(GameId)).Count);
        var doc = (JsonObject)JsonNode.Parse(await repo.ExportAsync(GameId, "3.11.0"))!;
        Assert.Equal(1, doc["export_v"]!.GetValue<int>());
        Assert.Equal("3.11.0", doc["app_version"]!.GetValue<string>());
        Assert.Equal(16, doc["schema_v"]!.GetValue<int>());
        Assert.True(doc["exported_at"]!.GetValue<long>() > 0);
        var items = (JsonArray)doc["items"]!;
        Assert.Equal(3, items.Count);
        var item = (JsonObject)items[0]!;
        Assert.Equal(a.CorrectionId, item["correction_id"]!.GetValue<string>());
        Assert.Equal(GameId, item["game_id"]!.GetValue<long>());
        Assert.Equal("det:DEATH:812:Lee Sin", item["subject_key"]!.GetValue<string>());
        Assert.Equal("DEATH", item["subject_type"]!.GetValue<string>());
        Assert.Equal(812, item["subject_time_s"]!.GetValue<int>());
        Assert.Equal("retime", item["op"]!.GetValue<string>());
        Assert.Equal(815, item["patch"]!["game_time_s"]!.GetValue<int>());
        Assert.False(item["patch"]!["attrs"]!["fog_death"]!.GetValue<bool>());
        Assert.Equal("DEATH", item["original"]!["event_type"]!.GetValue<string>());
        Assert.Equal("Lee Sin", item["original"]!["details"]!["killer"]!.GetValue<string>());
        Assert.Equal("Lee was pinged", item["reason"]!.GetValue<string>());
        Assert.Equal("map_state", item["detector"]!.GetValue<string>());
        Assert.Equal(3, item["delta_s"]!.GetValue<int>());
        Assert.Equal("reverted", item["state"]!.GetValue<string>());
        Assert.Contains(items, i => i!["op"]!.GetValue<string>() == "revert");
        Assert.Equal(3, ((JsonArray)JsonNode.Parse(await repo.ExportAsync(null, "dev"))!["items"]!).Count);
        Assert.Empty((JsonArray)JsonNode.Parse(await repo.ExportAsync(GameId + 1, "dev"))!["items"]!);
    }

    [Fact]
    public async Task StampMissingEventKeys_IsDeterministic_MatchesKeyForBatch_AndLeavesUsrKeys()
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope,
            Ev("KILL", 100, "{\"victim\":\"Jinx\"}"), Ev("KILL", 100, "{\"victim\":\"Jinx\"}"), Ev("DEATH", 200, "{\"killer\":\"Ahri\"}"),
            Ev("TEAMFIGHT", 610, "{\"start_s\":600,\"end_s\":640}"));
        var add = await repo.SaveAsync(Req(CorrectionOps.Add, null, new EventPatch("DRAGON", 1200, null, null)));
        const long other = GameId + 1;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(other));
        await scope.GameEvents.SaveEventsAsync(other, [new GameEvent { GameId = other, EventType = "KILL", GameTimeS = 50 }]);
        // Detected rows that predate the ledger (v3.11 inserts stamp event_key; the add keeps its usr: key).
        await Scalar(scope, "UPDATE game_events SET event_key = NULL WHERE event_key NOT LIKE 'usr:%'");

        // The limit is honoured per game: the first game (highest id) is finished, the next is not started.
        Assert.Equal(1, await repo.StampMissingEventKeysAsync(1));
        Assert.Equal(4, await repo.StampMissingEventKeysAsync(100));
        Assert.Equal(0, await repo.StampMissingEventKeysAsync(100));

        var rows = await RowsAsync(scope);
        var expected = EventIdentity.KeyForBatch(rows.Where(r => !EventIdentity.IsUserKey(r.EventKey)).ToList());
        Assert.Equal(expected, rows.Where(r => !EventIdentity.IsUserKey(r.EventKey)).Select(r => r.EventKey));
        Assert.Contains("det:KILL:100:Jinx", expected);
        Assert.Contains("det:KILL:100:Jinx#2", expected);
        Assert.Contains("det:TEAMFIGHT:600:", expected);
        Assert.Equal("usr:" + add.CorrectionId, Assert.Single(rows, r => r.EventType == "DRAGON").EventKey);
        Assert.Equal("det:KILL:50:", Assert.Single(await RowsAsync(scope, other)).EventKey);
    }

    [Fact]
    public async Task GameDelete_RemovesCorrections()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"));
        await repo.SaveAsync(Req(CorrectionOps.Retime, Subject(rows[0]), new EventPatch(null, 815, null, null)));
        Assert.Equal(1L, await Scalar(scope, "SELECT COUNT(*) FROM event_corrections"));

        await scope.Games.DeleteAsync(GameId);

        Assert.Equal(0L, await Scalar(scope, "SELECT COUNT(*) FROM event_corrections"));
        Assert.Empty(await RowsAsync(scope));
    }

    [Fact]
    public async Task ReviewedEncounters_Delegate_ReturnsAppliedIdAndKeepsEncounterMessages()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("TRADE", 110, "{\"kind\":\"extended\"}"), Ev("KILL", 115));
        var encounters = new ReviewedEncountersRepository(scope.ConnectionFactory);

        var addRequest = Guid.NewGuid().ToString("D");
        var addedId = await encounters.SaveAsync(GameId, null, addRequest, 191, 192, "short", "Q bounce");
        var added = Assert.Single(await RowsAsync(scope), r => r.Id == addedId);
        Assert.Equal("TRADE", added.EventType);
        Assert.Equal("usr:" + addRequest, added.EventKey);
        Assert.Equal(addedId, await encounters.SaveAsync(GameId, null, addRequest, 191, 192, "short", "Q bounce"));

        var trade = rows.Single(r => r.EventType == "TRADE");
        var convertedId = await encounters.SaveAsync(GameId, trade.Id, Guid.NewGuid().ToString("D"), 100, 114, "all_in", "Committed pursuit");
        Assert.Equal(trade.Id, convertedId);
        var converted = Assert.Single(await RowsAsync(scope), r => r.Id == trade.Id);
        Assert.Equal("ALL_IN", converted.EventType);
        Assert.Equal(100, converted.GameTimeS);
        Assert.Equal("det:TRADE:110:", converted.EventKey);
        var ledger = await repo.GetActiveForGameAsync(GameId);
        Assert.Equal(2, ledger.Count);
        Assert.Contains(ledger, c => c.Op == CorrectionOps.Add && c.SubjectKey == "usr:" + addRequest);
        Assert.Contains(ledger, c => c.Op == CorrectionOps.Retype && c.SubjectKey == "det:TRADE:110:" && c.Patch.EventType == "ALL_IN");

        // The same review again is a silent no-op that still returns the row id.
        Assert.Equal(trade.Id, await encounters.SaveAsync(GameId, trade.Id, Guid.NewGuid().ToString("D"), 100, 114, "all_in", "Committed pursuit"));
        Assert.Equal(2, (await repo.GetActiveForGameAsync(GameId)).Count);

        var kill = rows.Single(r => r.EventType == "KILL");
        var g = Guid.NewGuid().ToString("D");
        Assert.Equal("Only trade and combat encounter markers can be corrected.",
            (await Assert.ThrowsAsync<ArgumentException>(() => encounters.SaveAsync(GameId, kill.Id, g, 100, 101, "short", null))).Message);
        Assert.Equal("Choose short, extended, all_in or uncertain.",
            (await Assert.ThrowsAsync<ArgumentException>(() => encounters.SaveAsync(GameId, null, g, 100, 101, "guessed", null))).Message);
        Assert.Equal("Game not found.",
            (await Assert.ThrowsAsync<ArgumentException>(() => encounters.SaveAsync(GameId + 5, null, g, 100, 101, "short", null))).Message);
        Assert.Equal("Encounter extends past the game duration.",
            (await Assert.ThrowsAsync<ArgumentException>(() => encounters.SaveAsync(GameId, null, g, 100, 1900, "short", null))).Message);
        Assert.Equal("Encounter not found in this game.",
            (await Assert.ThrowsAsync<ArgumentException>(() => encounters.SaveAsync(GameId, 999_999, g, 100, 101, "short", null))).Message);
        Assert.Equal("Invalid encounter, time range or note.",
            (await Assert.ThrowsAsync<ArgumentException>(() => encounters.SaveAsync(GameId, null, g, 20, 10, "short", null))).Message);
        Assert.Equal(2, (await repo.GetActiveForGameAsync(GameId)).Count);
    }

    [Fact]
    public async Task Revert_RemoveOfUserAddedEvent_RestoresTheAddedRow()
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope, Ev("KILL", 900, "{\"victim\":\"Jinx\"}"));
        var add = await repo.SaveAsync(Req(CorrectionOps.Add, null, new EventPatch("TRADE", 310, 314, Attrs(("kind", "extended"))), "Q bounce"));
        var added = Assert.Single(await RowsAsync(scope), x => x.EventType == "TRADE");
        Assert.Equal("usr:" + add.CorrectionId, added.EventKey);

        // The form's "Remove (not real)" on the added marker: a remove row over the add.
        var remove = await repo.SaveAsync(Req(CorrectionOps.Remove, Subject(added)));
        Assert.DoesNotContain(await RowsAsync(scope), x => x.EventType == "TRADE");
        var replaced = await Assert.ThrowsAsync<ArgumentException>(() => repo.RevertAsync(GameId, add.CorrectionId));
        Assert.Equal(EventCorrectionsRepository.CorrectionReplacedMessage, replaced.Message);

        // Reverting the remove brings the added event back from the add it superseded.
        var r = await repo.RevertAsync(GameId, remove.CorrectionId);
        Assert.False(r.Idempotent);
        var restored = Assert.Single(await RowsAsync(scope), x => x.EventType == "TRADE");
        Assert.Equal(restored.Id, r.AppliedEventId);
        Assert.Equal(310, restored.GameTimeS);
        Assert.Equal("usr:" + add.CorrectionId, restored.EventKey);
        Assert.Equal(314, Details(restored)["end_s"]!.GetValue<int>());
        Assert.Equal("extended", Details(restored)["kind"]!.GetValue<string>());
        Assert.Equal("Q bounce", Details(restored)["note"]!.GetValue<string>());
        Assert.Equal((add.CorrectionId, "add"), EventPatching.ReadMarker(restored.Details));
        var active = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal(add.CorrectionId, active.CorrectionId);
        Assert.Equal(CorrectionStates.Active, active.State);
        Assert.Equal(restored.Id, active.AppliedEventId);
        Assert.Equal(CorrectionStates.Reverted, (await repo.GetForGameAsync(GameId)).Single(c => c.CorrectionId == remove.CorrectionId).State);

        // The add owns its undo again.
        var undone = await repo.RevertAsync(GameId, add.CorrectionId);
        Assert.False(undone.Idempotent);
        Assert.DoesNotContain(await RowsAsync(scope), x => x.EventType == "TRADE");
        Assert.Empty(await repo.GetActiveForGameAsync(GameId));
    }

    [Theory]
    [InlineData("all_in")]
    [InlineData("short")]
    public async Task ReviewedEncounters_NoteOnlyEdit_RewritesNote_WithoutLedgerRow(string classification)
    {
        using var scope = new TestDatabaseScope();
        var (repo, _) = await SeedAsync(scope, Ev("KILL", 900, "{\"victim\":\"Jinx\"}"));
        var encounters = new ReviewedEncountersRepository(scope.ConnectionFactory);
        var id = await encounters.SaveAsync(GameId, null, Guid.NewGuid().ToString("D"), 400, 410, classification, "first note");
        Assert.Single(await repo.GetForGameAsync(GameId));

        Assert.Equal(id, await encounters.SaveAsync(GameId, id, Guid.NewGuid().ToString("D"), 400, 410, classification, "actually I flashed first"));

        var row = Assert.Single(await RowsAsync(scope), x => x.Id == id);
        var d = Details(row);
        Assert.Equal("actually I flashed first", d["note"]!.GetValue<string>());
        Assert.Equal(classification, d["classification"]!.GetValue<string>());
        Assert.Equal(400, row.GameTimeS);
        Assert.Equal(410, d["end_s"]!.GetValue<int>());
        Assert.True(EventPatching.HasMarker(row.Details));
        Assert.Single(await repo.GetForGameAsync(GameId));
    }

    [Fact]
    public async Task ReviewedEncounters_ConfirmDetectedAtOwnWindow_MarksReviewed()
    {
        using var scope = new TestDatabaseScope();
        var (repo, rows) = await SeedAsync(scope, Ev("TRADE", 110, "{\"kind\":\"extended\",\"detected\":true}"));
        var encounters = new ReviewedEncountersRepository(scope.ConnectionFactory);
        var trade = rows.Single();
        var cid = Guid.NewGuid().ToString("D");

        Assert.Equal(trade.Id, await encounters.SaveAsync(GameId, trade.Id, cid, 110, 110, "extended", "Looks right"));

        var row = Assert.Single(await RowsAsync(scope), x => x.Id == trade.Id);
        Assert.NotNull(ReviewedEncountersRepository.ReviewDetails(row.Details));
        var d = Details(row);
        Assert.Equal("extended", d["kind"]!.GetValue<string>());
        Assert.Equal("extended", d["classification"]!.GetValue<string>());
        Assert.Equal("Looks right", d["note"]!.GetValue<string>());
        Assert.True(d["detected"]!.GetValue<bool>());
        Assert.Equal((cid, CorrectionOps.Confirm), EventPatching.ReadMarker(row.Details));
        var c = Assert.Single(await repo.GetActiveForGameAsync(GameId));
        Assert.Equal(CorrectionOps.Confirm, c.Op);
        Assert.True(c.Patch.Confirmed);
        Assert.Equal(trade.Id, c.AppliedEventId);
    }
}
