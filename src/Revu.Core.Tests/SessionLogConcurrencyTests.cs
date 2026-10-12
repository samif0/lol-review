namespace Revu.Core.Tests;

/// <summary>
/// session_log has no unique index on game_id, so LogGameAsync's check-then-insert must
/// be atomic: concurrent loggers for one game (game end racing a review save) must end
/// with one row, or every day stat counts the game twice.
/// </summary>
public sealed class SessionLogConcurrencyTests
{
    [Fact]
    public async Task ConcurrentLogGame_ForOneGame_WritesOneRow()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        long[] gameIds = [8301, 8302, 8303, 8304];
        foreach (var id in gameIds)
        {
            await scope.Games.SaveAsync(TestGameStatsFactory.Create(id));
        }

        await Task.WhenAll(gameIds.SelectMany(id => Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => scope.SessionLog.LogGameAsync(id, "Ahri", true, mentalRating: 1 + i)))));

        foreach (var id in gameIds)
        {
            Assert.Equal(1L, await CountAsync(scope, $"SELECT COUNT(*) FROM session_log WHERE game_id = {id}"));
        }
    }

    private static async Task<long> CountAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
