using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>Rule G, the startup sweep: NULL keys are stamped (capped, whole games at a time) and a
/// correction whose applied row vanished finds its twin again without a single insert.</summary>
public sealed class EventCorrectionSweepTests
{
    private static GameEvent Ev(long gameId, string type, int t, string details = "{}") =>
        new() { GameId = gameId, EventType = type, GameTimeS = t, Details = details };

    private static async Task<object?> ScalarAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<GameEvent>> RowsAsync(TestDatabaseScope scope, long gameId)
    {
        using var conn = scope.OpenConnection();
        return await EventCorrectionSql.LoadRowsAsync(conn, null, gameId);
    }

    [Fact]
    public async Task Run_StampsNullKeys_Capped_AndReportsCounts()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(7201));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(7202));
        await scope.GameEvents.SaveEventsAsync(7201, [Ev(7201, "KILL", 100, "{\"victim\":\"Jinx\"}"), Ev(7201, "DEATH", 200, "{\"killer\":\"Ahri\"}")]);
        await scope.GameEvents.SaveEventsAsync(7202,
            [Ev(7202, "DEATH", 300, "{\"killer\":\"Ahri\"}"), Ev(7202, "DEATH", 300, "{\"killer\":\"Ahri\"}"), Ev(7202, "DRAGON", 900)]);
        var expected7202 = (await RowsAsync(scope, 7202)).Select(x => x.EventKey).ToList();
        await ExecAsync(scope, "UPDATE game_events SET event_key = NULL");
        var sweep = new EventCorrectionSweep(scope.ConnectionFactory);
        Assert.Equal(5, await sweep.CountPendingAsync());

        // Newest game first; the cap is honoured only once the current game is complete.
        var first = await sweep.RunAsync(maxRows: 2);
        Assert.Equal(new EventCorrectionSweepResult(3, 0, 0), first);
        Assert.Equal(expected7202, (await RowsAsync(scope, 7202)).Select(x => x.EventKey));
        Assert.Equal(new[] { "det:DEATH:300:Ahri", "det:DEATH:300:Ahri#2", "det:DRAGON:900:" }, expected7202);
        Assert.All(await RowsAsync(scope, 7201), x => Assert.Null(x.EventKey));
        Assert.Equal(2, await sweep.CountPendingAsync());

        var second = await sweep.RunAsync();
        Assert.Equal(new EventCorrectionSweepResult(2, 0, 0), second);
        Assert.Equal(new[] { "det:KILL:100:Jinx", "det:DEATH:200:Ahri" }, (await RowsAsync(scope, 7201)).Select(x => x.EventKey));
        Assert.Equal(0, await sweep.CountPendingAsync());
        Assert.Equal(new EventCorrectionSweepResult(0, 0, 0), await sweep.RunAsync());
    }

    [Fact]
    public async Task Run_ResolvesOrphanWithoutInserting()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        const long gameId = 7203;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));
        await scope.GameEvents.SaveEventsAsync(gameId, [Ev(gameId, "DEATH", 812, "{\"killer\":\"Lee Sin\"}"), Ev(gameId, "KILL", 900)]);
        var death = (await RowsAsync(scope, gameId)).Single(x => x.EventType == "DEATH");
        var repo = new EventCorrectionsRepository(scope.ConnectionFactory);
        var r = await repo.SaveAsync(new EventCorrectionRequest(gameId, Guid.NewGuid().ToString("D"), CorrectionOps.Retime,
            new EventCorrectionSubject(death.EventKey, death.Id, "DEATH", 812), new EventPatch(null, 815, null, null), "Clock drift"));

        // A wipe and a raw re-import that bypassed rule A leave the correction dangling and the rows unkeyed.
        await scope.GameEvents.DeleteEventsAsync(gameId);
        await ExecAsync(scope, $"INSERT INTO game_events (game_id, event_type, game_time_s, details) VALUES ({gameId}, 'DEATH', 812, '{{\"killer\":\"Lee Sin\"}}'), ({gameId}, 'KILL', 900, '{{}}')");
        var sweep = new EventCorrectionSweep(scope.ConnectionFactory);
        Assert.Equal(3, await sweep.CountPendingAsync());

        var result = await sweep.RunAsync();

        Assert.Equal(new EventCorrectionSweepResult(2, 1, 1), result);
        var rows = await RowsAsync(scope, gameId);
        Assert.Equal(2, rows.Count);
        var resolved = Assert.Single(rows, x => x.EventType == "DEATH");
        Assert.Equal(815, resolved.GameTimeS);
        Assert.Equal("det:DEATH:812:Lee Sin", resolved.EventKey);
        Assert.Equal((r.CorrectionId, "retime"), EventPatching.ReadMarker(resolved.Details));
        var c = Assert.Single(await repo.GetActiveForGameAsync(gameId));
        Assert.Equal(CorrectionStates.Active, c.State);
        Assert.Equal(resolved.Id, c.AppliedEventId);
        Assert.Equal("", c.ApplyError);
        Assert.Equal(0, await sweep.CountPendingAsync());
        Assert.Equal(2L, await ScalarAsync(scope, $"SELECT COUNT(*) FROM game_events WHERE game_id = {gameId}"));
    }

    [Fact]
    public async Task CountPending_ZeroOnCleanDatabase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var sweep = new EventCorrectionSweep(scope.ConnectionFactory);
        Assert.Equal(0, await sweep.CountPendingAsync());

        const long gameId = 7204;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));
        await scope.GameEvents.SaveEventsAsync(gameId, [Ev(gameId, "DEATH", 812, "{\"killer\":\"Lee Sin\"}")]);
        var death = Assert.Single(await RowsAsync(scope, gameId));
        var repo = new EventCorrectionsRepository(scope.ConnectionFactory);
        await repo.SaveAsync(new EventCorrectionRequest(gameId, Guid.NewGuid().ToString("D"), CorrectionOps.Remove,
            new EventCorrectionSubject(death.EventKey, death.Id, "DEATH", 812), EventPatch.Empty, "Not a death"));
        await repo.SaveAsync(new EventCorrectionRequest(gameId, Guid.NewGuid().ToString("D"), CorrectionOps.Add, null,
            new EventPatch("DRAGON", 1200, null, null), ""));

        // Keyed rows, an applied add and a remove (which never has a row) are all settled.
        Assert.Equal(0, await sweep.CountPendingAsync());
        Assert.Equal(new EventCorrectionSweepResult(0, 0, 0), await sweep.RunAsync());
    }
}
