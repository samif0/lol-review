using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>
/// The Match library's Skip (SessionLogRepository.SetSkippedAsync): a skipped game leaves
/// the review queue without being reviewed, the skip holds even for a game that has no
/// session row, Unskip undoes it, the adherence streak is re-based (a skipped game's rule
/// trip is streak-neutral while the per-rule record keeps it), and the library totals count
/// skipped games apart from reviewed, hidden and remade ones.
/// </summary>
public sealed class ReviewSkipTests
{
    [Fact]
    public async Task SkippedGames_LeaveTheReviewQueue_AndUnskipBringsThemBack()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var now = DateTimeOffset.Now;
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6001, now.AddHours(-3));
            await InsertGameAsync(conn, 6002, now.AddHours(-2), win: false);
            await InsertGameAsync(conn, 6003, now.AddHours(-1));
        }
        await scope.SessionLog.LogGameAsync(6001, "Ahri", win: true);
        await scope.SessionLog.LogGameAsync(6002, "Ahri", win: false);
        // 6003 has no session row, like a legacy import.

        var applied = await scope.SessionLog.SetSkippedAsync(new long[] { 6001, 6003, 6001, 0, -4, 7777 }, skipped: true);

        Assert.Equal(new long[] { 6001, 6003 }, applied);   // once each; unknown and invalid ids are left alone
        Assert.Equal(new long[] { 6002 }, Ids(await scope.Games.GetUnreviewedGamesAsync(days: 14)));
        Assert.Equal(new long[] { 6001, 6003 },
            (await scope.Games.GetSkippedGameIdsAsync(new long[] { 6001, 6002, 6003 })).Order());

        Assert.Equal(new long[] { 6003 }, await scope.SessionLog.SetSkippedAsync(new long[] { 6003 }, skipped: false));
        Assert.Equal(new long[] { 6003, 6002 }, Ids(await scope.Games.GetUnreviewedGamesAsync(days: 14)));
        Assert.Equal(new long[] { 6001 }, await scope.Games.GetSkippedGameIdsAsync(new long[] { 6001, 6002, 6003 }));
        Assert.Empty(await scope.Games.GetSkippedGameIdsAsync(Array.Empty<long>()));
    }

    [Fact]
    public async Task SkippingAGameWithoutASessionRow_WritesTheRowGameEndWrites_MarkedSkipped()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var playedAt = new DateTimeOffset(DateTime.Today.AddDays(-3).AddHours(20));
        var date = playedAt.ToString("yyyy-MM-dd");
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6101, playedAt, champion: "Jinx", win: false);
        }

        await scope.SessionLog.SetSkippedAsync(new long[] { 6101 }, skipped: true);

        var entry = await scope.SessionLog.GetEntryAsync(6101);
        Assert.NotNull(entry);
        Assert.True(entry!.IsSkipped);
        Assert.Equal(date, entry.Date);
        Assert.Equal("Jinx", entry.ChampionName);
        Assert.False(entry.Win);
        Assert.Equal(playedAt.ToUnixTimeSeconds(), entry.Timestamp);
        Assert.Equal(5, entry.MentalRating);   // game end's default, never shown or counted while skipped
        Assert.Equal(0, entry.RuleBroken);
        Assert.DoesNotContain(6101L, (await scope.SessionLog.GetAllMentalRatingsAsync()).Keys);
        var day = await scope.SessionLog.GetStatsForDateAsync(date);
        Assert.Equal(1, day.Games);
        Assert.Equal(0.0, day.AvgMental);

        // Skipping again, unskipping and skipping once more never add a second row.
        await scope.SessionLog.SetSkippedAsync(new long[] { 6101 }, skipped: true);
        await scope.SessionLog.SetSkippedAsync(new long[] { 6101 }, skipped: false);
        Assert.False((await scope.SessionLog.GetEntryAsync(6101))!.IsSkipped);
        await scope.SessionLog.SetSkippedAsync(new long[] { 6101 }, skipped: true);
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM session_log WHERE game_id = 6101";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task UnskippingAGameWithoutASessionRow_WritesNothing()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6151, DateTimeOffset.Now.AddHours(-1));
        }

        Assert.Equal(new long[] { 6151 }, await scope.SessionLog.SetSkippedAsync(new long[] { 6151 }, skipped: false));
        Assert.Null(await scope.SessionLog.GetEntryAsync(6151));
        Assert.Contains(6151L, Ids(await scope.Games.GetUnreviewedGamesAsync(days: 14)));
    }

    [Fact]
    public async Task TheReviewPagesSkip_HoldsForAGameWithoutASessionRow()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 6201, DateTimeOffset.Now.AddHours(-1));
        }

        // Before, the skip updated no row and the game stayed in the queue.
        await scope.SessionLog.MarkSkippedAsync(6201);

        Assert.DoesNotContain(6201L, Ids(await scope.Games.GetUnreviewedGamesAsync(days: 14)));
        Assert.True((await scope.SessionLog.GetEntryAsync(6201))!.IsSkipped);
    }

    [Fact]
    public async Task LibraryTotals_CountSkippedGames_ButNeverReviewedHiddenOrRemadeOnes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var now = DateTimeOffset.Now;
        using (var conn = scope.OpenConnection())
        {
            for (var id = 6301L; id <= 6305; id++) await InsertGameAsync(conn, id, now.AddMinutes(id - 6400));
        }
        await scope.SessionLog.SetSkippedAsync(new long[] { 6301, 6302, 6303, 6304 }, skipped: true);
        // 6302 was reviewed after a skip some other way: the saved review wins.
        await scope.Games.UpdateReviewAsync(6302, new GameReview { Rating = 1, Notes = "Reviewed after all." });
        await scope.Games.SetHiddenAsync(6303, true);
        await scope.Games.SetRemakeVerdictAsync(6304, true, false, "eog");

        var totals = await scope.Games.GetLibraryTotalsAsync();

        Assert.Equal(new GameLibraryTotals(Games: 3, Wins: 3, Reviewed: 1, Skipped: 1), totals);
    }

    [Fact]
    public async Task SkippingATriggerGame_ReBasesTheStreak_AndUnskipRestoresTheTrip()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        var dayMinus2 = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            await InsertRuleAsync(conn, "Stop after 2 losses", "loss_streak", "2", dayMinus2.AddDays(-10).ToUnixTimeSeconds());
            // Day -2: W, L, L, then a loss played on a two-loss streak (the trip).
            await InsertGameAsync(conn, 6401, dayMinus2);
            await InsertGameAsync(conn, 6402, dayMinus2.AddMinutes(40), win: false);
            await InsertGameAsync(conn, 6403, dayMinus2.AddMinutes(80), win: false);
            await InsertGameAsync(conn, 6404, dayMinus2.AddMinutes(120), win: false);
            await InsertGameAsync(conn, 6405, dayMinus2.AddDays(1));
            await InsertGameAsync(conn, 6406, dayMinus2.AddDays(2), win: false);
        }
        var tripped = await rules.GetBehavioralAdherenceStreakAsync("2000-01-01");

        await scope.SessionLog.SetSkippedAsync(new long[] { 6404 }, skipped: true);
        Assert.Equal(3, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
        Assert.True(tripped < 3, $"the trip held the streak at {tripped}");
        // The per-rule record is an instrument: it keeps the skipped trip.
        var record = Assert.Single((await rules.GetRuleEvidenceAsync(await rules.GetAllAsync())).Values);
        Assert.Equal(1, record.TriggerGames);

        await scope.SessionLog.SetSkippedAsync(new long[] { 6404 }, skipped: false);
        Assert.Equal(tripped, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }

    private static long[] Ids(IEnumerable<GameStats> games) => games.Select(g => g.GameId).ToArray();

    private static async Task InsertGameAsync(SqliteConnection conn, long gameId, DateTimeOffset at,
        string champion = "Ahri", bool win = true)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, date_played, queue_type)
            VALUES (@gameId, @champion, @win, @timestamp, @datePlayed, 'Ranked Solo/Duo')";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@champion", champion);
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@timestamp", at.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@datePlayed", at.ToString("yyyy-MM-dd HH:mm"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertRuleAsync(SqliteConnection conn, string name, string ruleType, string conditionValue, long createdAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO rules (name, rule_type, condition_value, is_active, created_at)
            VALUES (@name, @ruleType, @conditionValue, 1, @createdAt)";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@ruleType", ruleType);
        cmd.Parameters.AddWithValue("@conditionValue", conditionValue);
        cmd.Parameters.AddWithValue("@createdAt", createdAt);
        await cmd.ExecuteNonQueryAsync();
    }
}
