using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>P-042: a tilt check entry can be deleted. The delete removes only the row with
/// that id, and every read (recent list, stats, latest plan) is computed from what remains,
/// so a stray entry stops skewing the stats and its plan stops coming back.</summary>
public sealed class TiltCheckRepositoryTests
{
    private const string CalmPlan = "If I fall two levels behind, then I farm for 60 seconds.";
    private const string StrayPlan = "If I lose, then I queue again right away.";
    private const string PregamePlan = "If I start to flame, then I mute all.";

    [Fact]
    public async Task Delete_RemovesOnlyThatRow_AndEveryReadRecomputes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new TiltCheckRepository(scope.ConnectionFactory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pregame = await SaveAtAsync(scope, repo, now - 3 * 3600, "pregame_plan", 0, null, PregamePlan);
        var calm = await SaveAtAsync(scope, repo, now - 2 * 3600, "anxious", 6, 3, CalmPlan);
        var stray = await SaveAtAsync(scope, repo, now - 3600, "frustrated", 10, 2, StrayPlan);

        Assert.Equal(StrayPlan, await repo.GetLatestPlanAsync());
        var before = await repo.GetStatsAsync();
        Assert.Equal(2, before.Total);
        Assert.Equal(8.0, before.AvgBefore);

        Assert.True(await repo.DeleteAsync(stray));

        Assert.Equal(new[] { calm, pregame }, Ids(await repo.GetRecentAsync()));
        var stats = await repo.GetStatsAsync();
        Assert.Equal(1, stats.Total);
        Assert.Equal(6.0, stats.AvgBefore);
        Assert.Equal(3.0, stats.AvgAfter);
        Assert.Equal(3.0, stats.AvgReduction);
        Assert.Equal(new[] { "anxious", "pregame_plan" }, stats.TopEmotions.Select(e => e.Emotion).Order(StringComparer.Ordinal));
        Assert.Equal(CalmPlan, await repo.GetLatestPlanAsync());

        // A pre-queue plan row is deleted the same way.
        Assert.True(await repo.DeleteAsync(pregame));
        Assert.Equal(new[] { calm }, Ids(await repo.GetRecentAsync()));
        Assert.Equal(new[] { new EmotionCount("anxious", 1) }, (await repo.GetStatsAsync()).TopEmotions);

        Assert.True(await repo.DeleteAsync(calm));
        Assert.Empty(await repo.GetRecentAsync());
        Assert.Null(await repo.GetLatestPlanAsync());
        var empty = await repo.GetStatsAsync();
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.TopEmotions);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse_AndKeepsEveryRow()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new TiltCheckRepository(scope.ConnectionFactory);
        var id = await repo.SaveAsync("tense", 7, 4);

        Assert.False(await repo.DeleteAsync(id + 1000));
        Assert.False(await repo.DeleteAsync(0));

        Assert.Equal(new[] { id }, Ids(await repo.GetRecentAsync()));
        Assert.Equal(1, (await repo.GetStatsAsync()).Total);
    }

    // created_at has one-second resolution, so each row gets an explicit, distinct time.
    private static async Task<long> SaveAtAsync(TestDatabaseScope scope, TiltCheckRepository repo, long createdAt,
        string emotion, int before, int? after, string plan)
    {
        var id = await repo.SaveAsync(emotion, before, after, ifThenPlan: plan);
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tilt_checks SET created_at = @createdAt WHERE id = @id";
        cmd.Parameters.AddWithValue("@createdAt", createdAt);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static long[] Ids(IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Select(row => Convert.ToInt64(row["id"])).ToArray();
}
