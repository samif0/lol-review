using Microsoft.Data.Sqlite;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.3 (schema v12): games played on a with-coach block day count as reviewed
/// — the coach reviews them outside Revu — so they leave the unreviewed queue
/// (dashboard nag / review-page subject / games Queue tab) WITHOUT touching
/// session_log (mental stats and streaks must stay byte-identical; the
/// exemption must never route through is_skipped). The map-state backfill
/// deliberately does NOT get the exemption: coach-block games still enrich, or
/// the derived-event data would develop a hole correlated with coach presence.
/// </summary>
public sealed class GameRepositoryCoachBlockExemptionTests
{
    [Fact]
    public async Task WithCoachBlockGames_LeaveUnreviewedQueue_SoloBlockGamesStay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        var yesterday = DateStr(DateTime.Today.AddDays(-1));

        // Today = a with-coach block; yesterday = a solo block.
        await scope.SessionLog.SetSessionIntentionAsync(today, "coach vod session", withCoach: true);
        await scope.SessionLog.SetSessionIntentionAsync(yesterday, "solo grind", withCoach: false);

        await InsertGameAsync(conn, gameId: 7001, datePlayed: today);
        await InsertGameAsync(conn, gameId: 7002, datePlayed: yesterday);
        // A game with NO sessions row at all — must also stay queued.
        await InsertGameAsync(conn, gameId: 7003, datePlayed: DateStr(DateTime.Today.AddDays(-2)));

        var queue = await scope.Games.GetUnreviewedGamesAsync(days: 3);
        var queuedIds = queue.Select(g => g.GameId).ToList();

        Assert.DoesNotContain(7001, queuedIds);   // coach block → exempt
        Assert.Contains(7002, queuedIds);          // solo block → still pending
        Assert.Contains(7003, queuedIds);          // no block → still pending
    }

    [Fact]
    public async Task WithCoachBlockGames_CountAsReviewed()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        await scope.SessionLog.SetSessionIntentionAsync(today, "coach vod session", withCoach: true);

        await InsertGameAsync(conn, gameId: 7101, datePlayed: today);
        await InsertGameAsync(conn, gameId: 7102, datePlayed: DateStr(DateTime.Today.AddDays(-1)));

        // Only the coach-block game counts as reviewed (the inverse predicate
        // must mirror the queue exactly, or count + queue stop summing).
        Assert.Equal(1, await scope.Games.GetReviewedCountAsync());
    }

    [Fact]
    public async Task MapStateBackfill_StillIncludesCoachBlockGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        await scope.SessionLog.SetSessionIntentionAsync(today, "coach vod session", withCoach: true);
        await InsertGameAsync(conn, gameId: 7201, datePlayed: today);

        // Deliberate divergence from the unreviewed predicate: the coach-block
        // game left the review queue but must still be in the map-state
        // missing set so its derived events get enriched like every other game.
        var missing = await scope.Games.GetGameIdsMissingMapStateAsync(MapStateAnalyzer.Version);
        Assert.Contains(7201L, missing);
    }

    [Fact]
    public async Task Exemption_LeavesSessionLogAndMentalStatsUntouched()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        await scope.SessionLog.SetSessionIntentionAsync(today, "coach vod session", withCoach: true);
        await InsertGameAsync(conn, gameId: 7301, datePlayed: today);
        await scope.SessionLog.LogGameAsync(7301, "Ahri", win: true, mentalRating: 8);

        // Out of the queue…
        var queue = await scope.Games.GetUnreviewedGamesAsync(days: 3);
        Assert.DoesNotContain(7301, queue.Select(g => g.GameId));

        // …but the session_log row is untouched: not skipped, and the mental
        // rating still feeds day stats (the whole point of not using is_skipped).
        var entry = await scope.SessionLog.GetEntryAsync(7301);
        Assert.NotNull(entry);
        var stats = await scope.SessionLog.GetStatsForDateAsync(today);
        Assert.Equal(1, stats.Games);
        Assert.Equal(8.0, stats.AvgMental);
        Assert.Contains(7301L, (await scope.SessionLog.GetAllMentalRatingsAsync()).Keys);
    }

    // ── Cross-midnight carry-over (the 23:00 coach session) ─────────────────

    [Fact]
    public async Task OpenCoachBlockFromYesterday_ExemptsPostMidnightGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        var yesterday = DateStr(DateTime.Today.AddDays(-1));

        // Coach block locked yesterday evening, never ended — it carries over
        // (same model as the dashboard's End Block carry-over). Its games that
        // finished after midnight are dated today and must stay exempt.
        await scope.SessionLog.SetSessionIntentionAsync(yesterday, "late coach session", withCoach: true);
        await InsertGameAsync(conn, gameId: 7401, datePlayed: today);

        var queue = await scope.Games.GetUnreviewedGamesAsync(days: 3);
        Assert.DoesNotContain(7401, queue.Select(g => g.GameId));
        Assert.Equal(1, await scope.Games.GetReviewedCountAsync());
    }

    [Fact]
    public async Task CoachBlockClosedAfterMidnight_KeepsExemptingThatNight()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        var yesterday = DateStr(DateTime.Today.AddDays(-1));

        // Block locked yesterday, ended at 01:40 today: the exemption must be
        // PERMANENT for the post-midnight games (not evaporate at End Block).
        await scope.SessionLog.SetSessionIntentionAsync(yesterday, "late coach session", withCoach: true);
        var endedAt = new DateTimeOffset(DateTime.Today.AddHours(1).AddMinutes(40)).ToUnixTimeSeconds();
        await SetBlockEndedAtAsync(conn, yesterday, endedAt);
        await InsertGameAsync(conn, gameId: 7501, datePlayed: today);

        var queue = await scope.Games.GetUnreviewedGamesAsync(days: 3);
        Assert.DoesNotContain(7501, queue.Select(g => g.GameId));
    }

    [Fact]
    public async Task CoachBlockClosedBeforeMidnight_DoesNotExemptTheNextDay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();

        var today = DateStr(DateTime.Today);
        var yesterday = DateStr(DateTime.Today.AddDays(-1));

        // Block locked AND ended yesterday (23:00): today's games are a new
        // day outside the coach session and must nag normally.
        await scope.SessionLog.SetSessionIntentionAsync(yesterday, "wrapped coach session", withCoach: true);
        var endedAt = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(23)).ToUnixTimeSeconds();
        await SetBlockEndedAtAsync(conn, yesterday, endedAt);
        await InsertGameAsync(conn, gameId: 7601, datePlayed: today);

        var queue = await scope.Games.GetUnreviewedGamesAsync(days: 3);
        Assert.Contains(7601, queue.Select(g => g.GameId));
    }

    // ── Raw-SQL seed (dates must be controlled; date_played drives the
    //    game→block mapping, timestamp keeps the row inside the queue window) ──

    private static string DateStr(DateTime d) => d.ToString("yyyy-MM-dd");

    private static async Task InsertGameAsync(SqliteConnection conn, long gameId, string datePlayed)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, date_played, queue_type)
            VALUES (@gameId, 'Ahri', 1, @timestamp, @datePlayed, 'Ranked Solo/Duo')";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@datePlayed", datePlayed);
        await cmd.ExecuteNonQueryAsync();
    }

    // SaveSessionDebriefAsync stamps ended_at with the real clock; tests pin
    // it directly so the midnight-boundary cases are deterministic.
    private static async Task SetBlockEndedAtAsync(SqliteConnection conn, string date, long endedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET ended_at = @endedAt WHERE date = @date";
        cmd.Parameters.AddWithValue("@endedAt", endedAt);
        cmd.Parameters.AddWithValue("@date", date);
        await cmd.ExecuteNonQueryAsync();
    }
}
