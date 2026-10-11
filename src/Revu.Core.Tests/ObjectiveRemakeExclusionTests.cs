using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>
/// 3.14.1: a remake never counts toward an objective: hit rate, mastery, score history,
/// the lowest-adherence focus seed and the every-startup game_count recompute all skip it.
/// </summary>
public sealed class ObjectiveRemakeExclusionTests
{
    [Fact]
    public async Task HitRateMasteryAndScoreHistory_ExcludeRemakes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river", completionCriteria: "deaths <= 3");
        var start = DateTimeOffset.Now.AddDays(-3);
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6001, win: true, start);
            await InsertGameAsync(conn, 6002, win: false, start.AddHours(1), duration: 386);
            await InsertGameAsync(conn, 6003, win: true, start.AddHours(2));
        }
        foreach (var (gameId, met) in new[] { (6001L, true), (6002L, false), (6003L, true) })
        {
            await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true);
            await scope.Objectives.SetCriteriaMetAsync(gameId, objectiveId, met);
        }
        Assert.Equal((2, 3), await scope.Objectives.GetCriteriaHitRateAsync(objectiveId));

        await MarkAsync(scope, 6002);

        Assert.Equal((2, 2), await scope.Objectives.GetCriteriaHitRateAsync(objectiveId));
        var mastery = await scope.Objectives.GetMasteryAsync(objectiveId, hasStructuredCriteria: true);
        Assert.Equal(2, mastery.QualifyingGames);
        Assert.Equal(1.0, mastery.Pct);
        Assert.Equal(2, (await scope.Objectives.GetScoreHistoryAsync(objectiveId)).Count);
        // The objective's game list still shows it, labelled.
        Assert.True(Assert.Single(await scope.Objectives.GetGamesForObjectiveAsync(objectiveId), g => g.GameId == 6002).IsRemake);
    }

    [Fact]
    public async Task ReviewingAMarkedRemake_DoesNotCountOrCompleteAMini()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Two-game drill");
        await SetMiniTargetAsync(scope, objectiveId, target: 2);
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6201, win: true, DateTimeOffset.Now.AddHours(-2));
            await InsertGameAsync(conn, 6202, win: false, DateTimeOffset.Now.AddHours(-1), duration: 386);
        }
        await scope.Objectives.RecordGameAsync(6201, objectiveId, practiced: true);
        await MarkAsync(scope, 6202);

        await scope.Objectives.RecordGameAsync(6202, objectiveId, practiced: true);

        var objective = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(1, objective.GameCount);
        Assert.Equal("active", objective.Status);
    }

    [Fact]
    public async Task AVerdictThatFlipsAfterTheReview_RebasesGameCount()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Crash wave");
        var otherId = await scope.Objectives.CreateAsync("Unrelated");
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6301, win: true, DateTimeOffset.Now.AddHours(-2));
            await InsertGameAsync(conn, 6302, win: false, DateTimeOffset.Now.AddHours(-1), duration: 386);
        }
        await scope.Objectives.RecordGameAsync(6301, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6302, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6301, otherId, practiced: true);
        Assert.Equal(2, (await scope.Objectives.GetAsync(objectiveId))!.GameCount);

        await MarkAsync(scope, 6302, true, "matchv5");
        Assert.Equal(1, (await scope.Objectives.GetAsync(objectiveId))!.GameCount);
        Assert.Equal(1, (await scope.Objectives.GetAsync(otherId))!.GameCount);

        await MarkAsync(scope, 6302, false, "user");
        Assert.Equal(2, (await scope.Objectives.GetAsync(objectiveId))!.GameCount);
    }

    private static async Task SetMiniTargetAsync(TestDatabaseScope scope, long objectiveId, int target)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE objectives SET type = 'mini', target_game_count = @t, status = 'active' WHERE id = @id";
        cmd.Parameters.AddWithValue("@t", target);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task StartupGameCountRecompute_ExcludesRemakes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Crash wave");
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6101, win: true, DateTimeOffset.Now.AddHours(-2));
            await InsertGameAsync(conn, 6102, win: false, DateTimeOffset.Now.AddHours(-1), duration: 300);
        }
        await scope.Objectives.RecordGameAsync(6101, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6102, objectiveId, practiced: true);
        await MarkAsync(scope, 6102);

        await scope.Initializer.ApplySeedsAndIndexesAsync();

        Assert.Equal(1, (await scope.Objectives.GetAsync(objectiveId))!.GameCount);
    }
}
