using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// P-028: the FOCUS CHECK answer (session_log.focus_adherence; 2=Yes / 1=Partly /
/// 0=No; null = unanswered) is written by set_focus_adherence but the review
/// snapshot never carried it back, so the gold selection vanished on re-render and
/// never preselected on load. The read path the fix relies on is
/// <see cref="Revu.Core.Data.Repositories.ISessionLogRepository.GetEntryAsync"/>
/// surfacing <c>FocusAdherence</c>. These pin the write → read round-trip for every
/// valid value (2/1/0) and the unanswered (null) default, so the value the review
/// form now reads is trustworthy.
/// </summary>
public sealed class SessionLogFocusAdherenceTests
{
    [Theory]
    [InlineData(2)] // Yes
    [InlineData(1)] // Partly
    [InlineData(0)] // No
    public async Task UpdateFocusAdherence_RoundTripsThroughGetEntry(int value)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 7101);
        // A session_log row must exist before focus_adherence can be set on it.
        await scope.SessionLog.LogGameAsync(7101, "Kai'Sa", win: true);

        await scope.SessionLog.UpdateFocusAdherenceAsync(7101, value);

        var entry = await scope.SessionLog.GetEntryAsync(7101);
        Assert.NotNull(entry);
        Assert.Equal(value, entry!.FocusAdherence);
    }

    [Fact]
    public async Task FocusAdherence_DefaultsToNull_WhenNeverAnswered()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 7102);
        await scope.SessionLog.LogGameAsync(7102, "Jinx", win: false);

        var entry = await scope.SessionLog.GetEntryAsync(7102);
        Assert.NotNull(entry);
        // Unanswered must read back as null (not 0 — 0 is the "No" answer).
        Assert.Null(entry!.FocusAdherence);
    }

    [Fact]
    public async Task UpdateFocusAdherence_CanBeClearedBackToNull()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        await InsertGameAsync(conn, gameId: 7103);
        await scope.SessionLog.LogGameAsync(7103, "Ezreal", win: true);

        await scope.SessionLog.UpdateFocusAdherenceAsync(7103, 2);
        await scope.SessionLog.UpdateFocusAdherenceAsync(7103, null); // re-tap clears it

        var entry = await scope.SessionLog.GetEntryAsync(7103);
        Assert.NotNull(entry);
        Assert.Null(entry!.FocusAdherence);
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
