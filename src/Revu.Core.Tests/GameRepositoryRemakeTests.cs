using Revu.Core.Data.Repositories;
using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>3.14.1: game_remakes reads, writes, precedence, deletes and the sweep's work list.</summary>
public sealed class GameRepositoryRemakeTests
{
    [Fact]
    public async Task Lists_LabelTheRemake_RuleAndReviewQueueReadsDropIt()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var now = DateTimeOffset.Now;
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 5001, win: true, now.AddMinutes(-60));
            await InsertGameAsync(conn, 5002, win: false, now.AddMinutes(-30), duration: 386);
        }
        await MarkAsync(scope, 5002);

        Assert.True((await scope.Games.GetAsync(5002))!.IsRemake);
        Assert.False((await scope.Games.GetAsync(5001))!.IsRemake);
        Assert.True(Assert.Single(await scope.Games.GetTodaysGamesAsync(), g => g.GameId == 5002).IsRemake);
        Assert.True(Assert.Single(await scope.Games.GetRecentAsync(), g => g.GameId == 5002).IsRemake);

        var ruleGames = await scope.Games.GetTodaysRuleGamesAsync();
        Assert.Equal(new long[] { 5001 }, ruleGames.Select(g => g.GameId));
        Assert.DoesNotContain(await scope.Games.GetUnreviewedGamesAsync(), g => g.GameId == 5002);
        Assert.Contains(await scope.Games.GetUnreviewedGamesAsync(), g => g.GameId == 5001);
    }

    [Fact]
    public async Task Precedence_UserIsSticky_MatchV5CorrectsCapture()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        Assert.Equal(new RemakeWriteResult(true, true), await MarkAsync(scope, 5101, true, "eog"));
        // Match-V5 outranks the capture read.
        Assert.Equal(new RemakeWriteResult(true, true), await MarkAsync(scope, 5101, false, "matchv5"));
        // A capture read cannot override Match-V5.
        Assert.Equal(new RemakeWriteResult(false, false), await MarkAsync(scope, 5101, true, "eog"));
        Assert.Equal(new RemakeWriteResult(false, false), await MarkAsync(scope, 5101, true, "lcu"));
        Assert.False((await scope.Games.GetRemakeVerdictAsync(5101))!.IsRemake);

        // The player's word wins and then holds against every automatic source.
        Assert.Equal(new RemakeWriteResult(true, true), await MarkAsync(scope, 5101, true, "user"));
        Assert.Equal(new RemakeWriteResult(false, false), await MarkAsync(scope, 5101, false, "matchv5"));
        Assert.Equal(new RemakeWriteResult(false, false), await MarkAsync(scope, 5101, false, "eog"));
        var verdict = await scope.Games.GetRemakeVerdictAsync(5101);
        Assert.True(verdict!.IsRemake);
        Assert.Equal("user", verdict.Source);

        // Unticking is the player's word too: sticky, so the game stays counted.
        Assert.Equal(new RemakeWriteResult(true, true), await MarkAsync(scope, 5101, false, "user"));
        Assert.Equal(new RemakeWriteResult(false, false), await MarkAsync(scope, 5101, true, "matchv5"));
    }

    [Fact]
    public async Task Changed_IsReportedOnlyOnAFlip()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        Assert.Equal(new RemakeWriteResult(true, false), await MarkAsync(scope, 5201, false, "eog"));
        Assert.Equal(new RemakeWriteResult(true, false), await MarkAsync(scope, 5201, false, "matchv5"));
        Assert.Equal(new RemakeWriteResult(true, true), await MarkAsync(scope, 5201, true, "matchv5"));
        Assert.Equal(new RemakeWriteResult(true, false), await MarkAsync(scope, 5201, true, "matchv5"));
        await Assert.ThrowsAsync<ArgumentException>(() => MarkAsync(scope, 5201, true, "guess"));
        await Assert.ThrowsAsync<ArgumentException>(() => MarkAsync(scope, 5202, true, "unknown"));
        // An unknown row never outranks a real verdict.
        Assert.Equal(new RemakeWriteResult(false, false), await MarkAsync(scope, 5201, false, "unknown"));
        Assert.True((await scope.Games.GetRemakeVerdictAsync(5201))!.IsRemake);
    }

    [Fact]
    public async Task Delete_RemovesTheVerdict()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using (var conn = scope.OpenConnection())
            await InsertGameAsync(conn, 5301, win: false, DateTimeOffset.Now, duration: 300);
        await MarkAsync(scope, 5301);

        await scope.Games.DeleteAsync(5301);

        Assert.Null(await scope.Games.GetRemakeVerdictAsync(5301));
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM game_remakes"));
    }

    [Fact]
    public async Task Candidates_ShortVisibleRankedWithoutAVerdict_NewestFirst_NoManualEntries()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var now = DateTimeOffset.Now;
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 5401, win: false, now.AddHours(-3), duration: 386);
            await InsertGameAsync(conn, 5402, win: false, now.AddHours(-2), duration: 599);
            await InsertGameAsync(conn, 5403, win: false, now.AddHours(-1), duration: 600);          // too long
            await InsertGameAsync(conn, 5404, win: false, now.AddMinutes(-50), duration: 300, hidden: true);
            await InsertGameAsync(conn, 5405, win: false, now.AddMinutes(-40), duration: 300, queue: "ARAM");
            await InsertGameAsync(conn, 5406, win: false, now.AddMinutes(-30), duration: 300);       // has a verdict
            // Manual entries (timestamp ids, duration 0) are never looked up.
            await InsertGameAsync(conn, 5407, win: false, now.AddMinutes(-20), duration: 450, queue: "Manual");
            await InsertGameAsync(conn, 5408, win: false, now.AddMinutes(-10), duration: 0, queue: "Manual");
            await InsertGameAsync(conn, 5409, win: false, now.AddMinutes(-5), duration: 0);            // no duration
        }
        await MarkAsync(scope, 5406, isRemake: false);

        var candidates = await scope.Games.GetRemakeCandidatesAsync(600, 25);

        Assert.Equal(new long[] { 5402, 5401 }, candidates.Select(c => c.GameId));
        Assert.Equal(386, candidates[^1].Duration);
        Assert.Equal(2, (await scope.Games.GetRemakeCandidatesAsync(600, 2)).Count);
    }

    [Fact]
    public async Task Writes_SelfHealAMissingTable()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE game_remakes";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Null(await scope.Games.GetRemakeVerdictAsync(5501));
        Assert.Empty(await scope.Games.GetRemakeCandidatesAsync(600, 25));

        var result = await MarkAsync(scope, 5501, true, "user");

        Assert.True(result.Applied);
        Assert.True((await scope.Games.GetRemakeVerdictAsync(5501))!.IsRemake);
    }
}
