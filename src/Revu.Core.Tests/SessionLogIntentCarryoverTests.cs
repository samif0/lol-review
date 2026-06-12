using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// v2.18 (schema v6): the pre-game intent carry-over write path
/// (digest 2026-06-11-2 P2). session_log.pregame_intention/intention_source
/// were dropped by the C# rewrite (write-path audit, brief 2026-06-11-03) —
/// these tests pin the re-added INSERT/UPDATE/read path, and the guard that
/// keeps the review-save re-log (which passes defaults) from clobbering what
/// end-of-game wrote.
/// </summary>
public sealed class SessionLogIntentCarryoverTests
{
    [Fact]
    public async Task LogGame_PersistsIntentionSourceAndMood_RoundTrip()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 9001);

        await scope.SessionLog.LogGameAsync(
            9001, "Kai'Sa", win: true,
            preGameMood: 4,
            pregameIntention: "Stop shoving past river without jungle tracked",
            intentionSource: "carry");

        var entry = await scope.SessionLog.GetEntryAsync(9001);

        Assert.NotNull(entry);
        Assert.Equal("Stop shoving past river without jungle tracked", entry!.PregameIntention);
        Assert.Equal("carry", entry.IntentionSource);
        Assert.Equal(4, entry.PreGameMood);
    }

    [Fact]
    public async Task LogGame_UpdateWithDefaults_DoesNotClobberPreGameStamps()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 9002);

        // End-of-game stamps the pre-game snapshot...
        await scope.SessionLog.LogGameAsync(
            9002, "Kai'Sa", win: false,
            preGameMood: 2,
            pregameIntention: "Hit 8.0 cs/min by 15:00",
            intentionSource: "objective");

        // ...then the review save re-logs the same game with defaults
        // (mentalRating + note only — the shape ReviewWorkflowService uses).
        await scope.SessionLog.LogGameAsync(
            9002, "Kai'Sa", win: false,
            mentalRating: 7,
            improvementNote: "Track jungle before pushing");

        var entry = await scope.SessionLog.GetEntryAsync(9002);

        Assert.NotNull(entry);
        Assert.Equal(7, entry!.MentalRating);
        Assert.Equal("Track jungle before pushing", entry.ImprovementNote);
        // The defaults must not have erased the end-of-game stamps.
        Assert.Equal("Hit 8.0 cs/min by 15:00", entry.PregameIntention);
        Assert.Equal("objective", entry.IntentionSource);
        Assert.Equal(2, entry.PreGameMood);
    }

    [Fact]
    public async Task LogGame_UpdateWithNewIntention_Replaces()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 9003);

        await scope.SessionLog.LogGameAsync(
            9003, "Jinx", win: true,
            pregameIntention: "Ward river by 2:45",
            intentionSource: "carry");

        await scope.SessionLog.LogGameAsync(
            9003, "Jinx", win: true,
            pregameIntention: "Play for picks with support",
            intentionSource: "edited");

        var entry = await scope.SessionLog.GetEntryAsync(9003);

        Assert.NotNull(entry);
        Assert.Equal("Play for picks with support", entry!.PregameIntention);
        Assert.Equal("edited", entry.IntentionSource);
    }

    private static async Task InsertGameAsync(SqliteConnection conn, long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, queue_type, is_hidden)
            VALUES (@gameId, 'Kai''Sa', 1, @timestamp, 'Ranked Solo/Duo', 0)";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.Now.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }
}
