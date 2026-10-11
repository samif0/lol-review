using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>
/// 3.14.1: a remake's session_log row never feeds day stats, summaries, mental
/// trends, tilt warnings or session patterns (mirrors the hidden-games tests).
/// Manual rows (game_id NULL) and a recorded "not a remake" (is_remake=0) still count.
/// </summary>
public sealed class SessionLogRepositoryRemakeTests
{
    private static async Task<TestDatabaseScope> SeedAsync()
    {
        var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var now = DateTimeOffset.Now;
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 4001, win: true, now.AddMinutes(-90));
            await InsertSessionRowAsync(conn, Today, 4001, win: true, mental: 8, at: now.AddMinutes(-90));
            // The remake: a loss with a rule break and a big mental drop. It must vanish.
            await InsertGameAsync(conn, 4002, win: false, now.AddMinutes(-60), duration: 386);
            await InsertSessionRowAsync(conn, Today, 4002, win: false, mental: 2, ruleBroken: true, at: now.AddMinutes(-60));
            // A game with an explicit negative verdict counts like any other.
            await InsertGameAsync(conn, 4003, win: true, now.AddMinutes(-30));
            await InsertSessionRowAsync(conn, Today, 4003, win: true, mental: 7, at: now.AddMinutes(-30));
            // A manual row (no games row) keeps counting.
            await InsertSessionRowAsync(conn, Today, null, win: true, mental: 6, at: now.AddMinutes(-10));
        }
        await MarkAsync(scope, 4002);
        await MarkAsync(scope, 4003, isRemake: false);
        return scope;
    }

    [Fact]
    public async Task DayStatsAndSummaries_ExcludeTheRemake()
    {
        using var scope = await SeedAsync();

        var stats = await scope.SessionLog.GetStatsForDateAsync(Today);
        Assert.Equal(3, stats.Games);
        Assert.Equal(3, stats.Wins);
        Assert.Equal(0, stats.Losses);
        Assert.Equal(0, stats.RuleBreaks);

        var day = Assert.Single(await scope.SessionLog.GetDailySummariesAsync(days: 7), s => s.Date == Today);
        Assert.Equal(3, day.Games);
        Assert.Equal(0, day.Losses);
    }

    [Fact]
    public async Task DayLists_ExcludeTheRemake_KeepManualAndNegativeVerdictRows()
    {
        using var scope = await SeedAsync();

        var entries = await scope.SessionLog.GetForDateAsync(Today);
        Assert.Equal(3, entries.Count);
        Assert.DoesNotContain(entries, e => e.GameId == 4002);
        Assert.Contains(entries, e => e.GameId == 4003);
        Assert.Contains(entries, e => e.GameId is null);
        Assert.Equal(3, (await scope.SessionLog.GetTodayAsync()).Count);
    }

    [Fact]
    public async Task MentalTrendTiltAndPatterns_ExcludeTheRemake()
    {
        using var scope = await SeedAsync();

        var trend = await scope.SessionLog.GetMentalTrendAsync();
        Assert.DoesNotContain(trend, p => p.MentalRating == 2);
        Assert.Equal(3, trend.Count);

        // 8 -> 2 would warn; with the remake gone the day reads 8 -> 7 -> 6.
        Assert.Null(await scope.SessionLog.CheckTiltWarningAsync(Today));

        var patterns = await scope.SessionLog.GetSessionPatternsAsync();
        Assert.Equal(3.0, patterns.AvgGamesPerSession);
        Assert.Equal(1, patterns.TotalSessionDays);
    }

    [Fact]
    public async Task RemakeOnlyDay_IsNotAPlayDay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var yesterday = DateTimeOffset.Now.AddDays(-1);
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 4101, win: false, yesterday, duration: 300);
            await InsertSessionRowAsync(conn, yesterday.ToString("yyyy-MM-dd"), 4101, win: false, at: yesterday);
        }
        await MarkAsync(scope, 4101);

        Assert.DoesNotContain(yesterday.ToString("yyyy-MM-dd"), await scope.SessionLog.GetDatesWithGamesAsync());
    }
}
