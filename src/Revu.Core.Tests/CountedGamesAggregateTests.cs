using Revu.Core.Data.Repositories;
using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>
/// The dashboard death mix, the death count behind it and the Patterns saved-moment
/// cards count only counted games (visible and not a remake).
/// </summary>
public sealed class CountedGamesAggregateTests
{
    [Fact]
    public async Task DeathMixAndDeathCount_SkipRemakes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var repo = new DeathClassificationsRepository(scope.ConnectionFactory);
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6701, win: true, DateTimeOffset.Now.AddHours(-2));
            await InsertGameAsync(conn, 6702, win: false, DateTimeOffset.Now.AddHours(-1), duration: 300);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO game_events (game_id, event_type, game_time_s) VALUES (6701, 'DEATH', 300), (6702, 'DEATH', 120);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        await repo.UpsertAsync(6701, 300, DeathClasses.Wave);
        await repo.UpsertAsync(6702, 120, DeathClasses.Wave);
        Assert.Equal(2, (await repo.GetClassMixAsync(days: 14)).Sum(m => m.Count));
        Assert.Equal(2, await repo.GetDeathEventCountAsync(days: 14));

        await MarkAsync(scope, 6702);

        Assert.Equal(1, (await repo.GetClassMixAsync(days: 14)).Sum(m => m.Count));
        Assert.Equal(1, await repo.GetDeathEventCountAsync(days: 14));
    }

    [Fact]
    public async Task PatternCards_SkipMomentsSavedOnARemake()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6711, win: true, DateTimeOffset.Now.AddHours(-2));
            await InsertGameAsync(conn, 6712, win: false, DateTimeOffset.Now.AddHours(-1), duration: 300);
        }
        await scope.Vod.AddBookmarkAsync(6711, 150, "missed reset", objectiveId: objectiveId);
        await scope.Vod.AddBookmarkAsync(6712, 90, "remake moment", objectiveId: objectiveId);
        Assert.Equal(2, Assert.Single(await scope.Evidence.GetPatternCardsAsync()).MomentCount);

        await MarkAsync(scope, 6712);

        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(1, card.MomentCount);
        Assert.Equal(1, card.GameCount);
    }
}
