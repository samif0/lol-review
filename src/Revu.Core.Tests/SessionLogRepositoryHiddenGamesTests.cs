using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// games.is_hidden = 1 rows are soft-removed (user-hidden or legacy
/// cross-account imports). Their session_log rows must not feed day stats
/// or daily summaries — while manual session rows (game_id NULL, no games
/// row) must keep counting.
/// </summary>
public sealed class SessionLogRepositoryHiddenGamesTests
{
    [Fact]
    public async Task GetStatsForDate_ExcludesHiddenGames_KeepsManualRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var date = DateStr(DateTime.Today);

        await InsertGameAsync(conn, gameId: 2001, win: true, hidden: false);
        await InsertSessionRowAsync(conn, date, gameId: 2001, win: true, mentalRating: 8);

        // Hidden loss with a rule break — must vanish from every aggregate.
        await InsertGameAsync(conn, gameId: 2002, win: false, hidden: true);
        await InsertSessionRowAsync(conn, date, gameId: 2002, win: false, ruleBroken: true, mentalRating: 2);

        // Manual log with no games row — LEFT JOIN must keep it.
        await InsertSessionRowAsync(conn, date, gameId: null, win: true, mentalRating: 6);

        var stats = await scope.SessionLog.GetStatsForDateAsync(date);

        Assert.Equal(2, stats.Games);
        Assert.Equal(2, stats.Wins);
        Assert.Equal(0, stats.Losses);
        Assert.Equal(0, stats.RuleBreaks);
        Assert.Equal(7.0, stats.AvgMental);
    }

    [Fact]
    public async Task GetDailySummaries_ExcludeHiddenGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var date = DateStr(DateTime.Today);

        await InsertGameAsync(conn, gameId: 3001, win: true, hidden: false);
        await InsertSessionRowAsync(conn, date, gameId: 3001, win: true);

        await InsertGameAsync(conn, gameId: 3002, win: false, hidden: true);
        await InsertSessionRowAsync(conn, date, gameId: 3002, win: false, ruleBroken: true);

        var summaries = await scope.SessionLog.GetDailySummariesAsync(days: 7);

        var day = Assert.Single(summaries, s => s.Date == date);
        Assert.Equal(1, day.Games);
        Assert.Equal(1, day.Wins);
        Assert.Equal(0, day.Losses);
        Assert.Equal(0, day.RuleBreaks);
    }

    // ── Raw-SQL helpers (dates must be controlled, LogGameAsync stamps today) ──

    private static string DateStr(DateTime d) => d.ToString("yyyy-MM-dd");

    private static async Task InsertGameAsync(SqliteConnection conn, long gameId, bool win, bool hidden)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, queue_type, is_hidden)
            VALUES (@gameId, 'Ahri', @win, @timestamp, 'Ranked Solo/Duo', @hidden)";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@hidden", hidden ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSessionRowAsync(
        SqliteConnection conn,
        string date,
        long? gameId,
        bool win,
        bool ruleBroken = false,
        int mentalRating = 5)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO session_log (date, game_id, champion_name, win, mental_rating, rule_broken, timestamp)
            VALUES (@date, @gameId, 'Ahri', @win, @mental, @ruleBroken, @timestamp)";
        cmd.Parameters.AddWithValue("@date", date);
        cmd.Parameters.AddWithValue("@gameId", gameId.HasValue ? gameId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@mental", mentalRating);
        cmd.Parameters.AddWithValue("@ruleBroken", ruleBroken ? 1 : 0);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }
}
