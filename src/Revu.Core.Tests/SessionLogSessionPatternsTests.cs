using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// GetSessionPatternsAsync computes the day "mental trajectory": the average
/// of (last game's mental − first game's mental) per play-day. The original
/// query combined FIRST_VALUE windows with GROUP BY sl.date — SQLite runs
/// window functions AFTER grouping, so every day collapsed to one row and the
/// delta always read 0. These tests pin the corrected per-row window shape.
/// </summary>
public sealed class SessionLogSessionPatternsTests
{
    [Fact]
    public async Task GetSessionPatterns_AvgMentalDelta_IsLastMinusFirstPerDay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        // Day 1: 8 → 5 → 2 across the session (delta −6).
        await InsertSessionRowAsync(conn, "2026-01-10", 9_201, mentalRating: 8, timestamp: 1_000);
        await InsertSessionRowAsync(conn, "2026-01-10", 9_202, mentalRating: 5, timestamp: 2_000);
        await InsertSessionRowAsync(conn, "2026-01-10", 9_203, mentalRating: 2, timestamp: 3_000);

        // Day 2: 4 → 6 (delta +2).
        await InsertSessionRowAsync(conn, "2026-01-11", 9_204, mentalRating: 4, timestamp: 10_000);
        await InsertSessionRowAsync(conn, "2026-01-11", 9_205, mentalRating: 6, timestamp: 11_000);

        var patterns = await scope.SessionLog.GetSessionPatternsAsync();

        // (−6 + 2) / 2 = −2.0
        Assert.Equal(-2.0, patterns.AvgMentalDelta);
        Assert.Equal(2, patterns.TotalSessionDays);
    }

    [Fact]
    public async Task GetSessionPatterns_SkippedGames_DoNotAnchorTheDelta()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        // The skipped last game carries a default rating — it must not become
        // the day's "last mental".
        await InsertSessionRowAsync(conn, "2026-01-12", 9_301, mentalRating: 3, timestamp: 1_000);
        await InsertSessionRowAsync(conn, "2026-01-12", 9_302, mentalRating: 7, timestamp: 2_000);
        await InsertSessionRowAsync(conn, "2026-01-12", 9_303, mentalRating: 5, timestamp: 3_000, skipped: true);

        var patterns = await scope.SessionLog.GetSessionPatternsAsync();

        Assert.Equal(4.0, patterns.AvgMentalDelta);
    }

    private static async Task InsertSessionRowAsync(
        SqliteConnection conn,
        string date,
        long gameId,
        int mentalRating,
        long timestamp,
        bool skipped = false)
    {
        // session_log.game_id has an enforced FK to games(game_id) in the test
        // scope, so a matching games row must exist first.
        using (var gameCmd = conn.CreateCommand())
        {
            gameCmd.CommandText = @"
                INSERT INTO games (game_id, champion_name, win, timestamp, queue_type)
                VALUES (@gameId, 'Ahri', 1, @timestamp, 'Ranked Solo/Duo')";
            gameCmd.Parameters.AddWithValue("@gameId", gameId);
            gameCmd.Parameters.AddWithValue("@timestamp", timestamp);
            await gameCmd.ExecuteNonQueryAsync();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO session_log (date, game_id, champion_name, win, mental_rating, timestamp, is_skipped)
            VALUES (@date, @gameId, 'Ahri', 1, @mental, @timestamp, @skipped)";
        cmd.Parameters.AddWithValue("@date", date);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@mental", mentalRating);
        cmd.Parameters.AddWithValue("@timestamp", timestamp);
        cmd.Parameters.AddWithValue("@skipped", skipped ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }
}
