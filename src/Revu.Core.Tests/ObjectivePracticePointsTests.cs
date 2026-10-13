using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>
/// Practice points (2 per practiced game) come only from counted games: a remake never
/// earns them, and a game that becomes a remake, is hidden or is deleted takes exactly
/// its own points with it (restoring brings them back). Clip points the score also
/// holds are never touched.
/// </summary>
public sealed class ObjectivePracticePointsTests
{
    [Fact]
    public async Task PracticingARemake_EarnsNoPoints()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6501, win: false, DateTimeOffset.Now.AddHours(-1), duration: 300);
        }
        await MarkAsync(scope, 6501);

        await scope.Objectives.RecordGameAsync(6501, objectiveId, practiced: true);

        Assert.Equal(0, (await scope.Objectives.GetAsync(objectiveId))!.Score);
    }

    [Fact]
    public async Task RemakeVerdict_MovesThePracticePoints_BothWays()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6511, 6512);
        await scope.Objectives.RecordGameAsync(6511, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6512, objectiveId, practiced: true);
        await AddClipPointsAsync(scope, objectiveId, 4);
        Assert.Equal(8, await ScoreAsync(scope, objectiveId));

        await MarkAsync(scope, 6512, true, "matchv5");
        Assert.Equal(6, await ScoreAsync(scope, objectiveId));

        await MarkAsync(scope, 6512, false, "user");
        Assert.Equal(8, await ScoreAsync(scope, objectiveId));
    }

    [Fact]
    public async Task HideAndRestore_MoveThePracticePointsAndGameCount()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        var unpracticedId = await scope.Objectives.CreateAsync("Farm");
        await SeedAsync(scope, 6521, 6522);
        await scope.Objectives.RecordGameAsync(6521, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6522, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6522, unpracticedId, practiced: false);

        await scope.Games.SetHiddenAsync(6522, true);
        var hidden = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(2, hidden.Score);
        Assert.Equal(1, hidden.GameCount);
        Assert.Equal(0, (await scope.Objectives.GetAsync(unpracticedId))!.GameCount);

        // Hiding twice must not take the points twice.
        await scope.Games.SetHiddenAsync(6522, true);
        Assert.Equal(2, await ScoreAsync(scope, objectiveId));

        // A review re-saved while hidden earns nothing either.
        await scope.Objectives.RecordGameAsync(6522, objectiveId, practiced: true, "re-saved");
        Assert.Equal(2, await ScoreAsync(scope, objectiveId));

        await scope.Games.SetHiddenAsync(6522, false);
        var restored = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(4, restored.Score);
        Assert.Equal(2, restored.GameCount);
    }

    [Fact]
    public async Task HidingARemake_DoesNotTakePointsItNeverHad()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6531, 6532);
        await scope.Objectives.RecordGameAsync(6531, objectiveId, practiced: true);
        await MarkAsync(scope, 6532);
        await scope.Objectives.RecordGameAsync(6532, objectiveId, practiced: true);

        await scope.Games.SetHiddenAsync(6532, true);

        Assert.Equal(2, await ScoreAsync(scope, objectiveId));
    }

    [Fact]
    public async Task DeletingAGame_TakesItsPointsAndRebasesTheGameCount()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6541, 6542);
        await scope.Objectives.RecordGameAsync(6541, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6542, objectiveId, practiced: true);
        await AddClipPointsAsync(scope, objectiveId, 2);

        await scope.Games.DeleteAsync(6542);

        var objective = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(4, objective.Score);
        Assert.Equal(1, objective.GameCount);
    }

    [Fact]
    public async Task StartupScoreFloor_DoesNotHandBackRemakePoints()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6551);
        await scope.Objectives.RecordGameAsync(6551, objectiveId, practiced: true);
        await MarkAsync(scope, 6551);
        Assert.Equal(0, await ScoreAsync(scope, objectiveId));

        await scope.Initializer.ApplySeedsAndIndexesAsync();

        Assert.Equal(0, await ScoreAsync(scope, objectiveId));
    }

    [Fact]
    public async Task HideAndDelete_TolerateADatabaseWithoutTheRemakesTable()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6561, 6562);
        await scope.Objectives.RecordGameAsync(6561, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6562, objectiveId, practiced: true);

        await DropRemakesTableAsync(scope);
        await scope.Games.SetHiddenAsync(6561, true);
        var hidden = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(2, hidden.Score);
        Assert.Equal(1, hidden.GameCount);

        await DropRemakesTableAsync(scope);
        await scope.Games.DeleteAsync(6562);
        var deleted = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(0, deleted.Score);
        Assert.Equal(0, deleted.GameCount);
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM games WHERE game_id = 6562"));
    }

    [Fact]
    public async Task UpgradedDatabase_OldHiddenGamesAndRemakes_AreCreditedOnlyOnce()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6601, 6602, 6603);
        foreach (var id in new long[] { 6601, 6602, 6603 })
            await scope.Objectives.RecordGameAsync(id, objectiveId, practiced: true);
        await AddClipPointsAsync(scope, objectiveId, 4);
        // What 3.14.1 left: 6602 hidden and 6603 marked a remake, both still holding points.
        await HideAsOlderBuildAsync(scope, 6602);
        await MarkRemakeAsOlderBuildAsync(scope, 6603);
        await ForgetRebaseAsync(scope);
        Assert.Equal(10, await ScoreAsync(scope, objectiveId));

        await scope.Initializer.ApplySeedsAndIndexesAsync();
        Assert.Equal(6, await ScoreAsync(scope, objectiveId));

        await scope.Games.SetHiddenAsync(6602, false);
        await MarkAsync(scope, 6603, false, "user");
        var objective = (await scope.Objectives.GetAsync(objectiveId))!;
        Assert.Equal(10, objective.Score);
        Assert.Equal(3, objective.GameCount);

        // The re-base runs once per database: a later launch never takes points again.
        await scope.Games.SetHiddenAsync(6601, true);
        await scope.Initializer.ApplySeedsAndIndexesAsync();
        Assert.Equal(8, await ScoreAsync(scope, objectiveId));
    }

    [Fact]
    public async Task UpgradedDatabase_OldHiddenGameResavedUnpracticed_KeepsNoStalePoints()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        await SeedAsync(scope, 6611, 6612);
        await scope.Objectives.RecordGameAsync(6611, objectiveId, practiced: true);
        await scope.Objectives.RecordGameAsync(6612, objectiveId, practiced: true);
        await HideAsOlderBuildAsync(scope, 6612);
        await ForgetRebaseAsync(scope);
        await scope.Initializer.ApplySeedsAndIndexesAsync();
        Assert.Equal(2, await ScoreAsync(scope, objectiveId));

        await scope.Objectives.RecordGameAsync(6612, objectiveId, practiced: false, "re-saved");
        await scope.Games.SetHiddenAsync(6612, false);
        Assert.Equal(2, await ScoreAsync(scope, objectiveId));

        await scope.Objectives.RecordGameAsync(6612, objectiveId, practiced: true, "practiced after all");
        Assert.Equal(4, await ScoreAsync(scope, objectiveId));
    }

    // Builds up to 3.14.1 hid a game and stored a remake verdict without moving any points.
    private static async Task HideAsOlderBuildAsync(TestDatabaseScope scope, long gameId)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET is_hidden = 1 WHERE game_id = @id";
        cmd.Parameters.AddWithValue("@id", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task MarkRemakeAsOlderBuildAsync(TestDatabaseScope scope, long gameId)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_remakes (game_id, is_remake, caused_by_self, source, checked_at)
            VALUES (@id, 1, 0, 'eog', 0)
            """;
        cmd.Parameters.AddWithValue("@id", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    // A database an older build last opened has never been re-based.
    private static async Task ForgetRebaseAsync(TestDatabaseScope scope)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM schema_metadata WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", Revu.Core.Data.DatabaseInitializer.PracticePointsRebasedKey);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    // A database a downgraded build last opened, or one whose startup schema pass failed.
    private static async Task DropRemakesTableAsync(TestDatabaseScope scope)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS game_remakes";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedAsync(TestDatabaseScope scope, params long[] gameIds)
    {
        using var conn = scope.OpenConnection();
        var at = DateTimeOffset.Now.AddHours(-gameIds.Length - 1);
        foreach (var id in gameIds)
        {
            at = at.AddMinutes(40);
            await InsertGameAsync(conn, id, win: true, at);
        }
    }

    // Stands in for clip-attach points, which the score holds but nothing can recompute.
    private static async Task AddClipPointsAsync(TestDatabaseScope scope, long objectiveId, int points)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE objectives SET score = score + @p WHERE id = @id";
        cmd.Parameters.AddWithValue("@p", points);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScoreAsync(TestDatabaseScope scope, long objectiveId) =>
        (await scope.Objectives.GetAsync(objectiveId))!.Score;
}
