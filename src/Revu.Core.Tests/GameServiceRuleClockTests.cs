using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// The post-game rule stamp judges clock rules at the game's start (when it was queued),
/// like the Rules evidence pass, not at game end or at a later ingest.
/// </summary>
public sealed class GameServiceRuleClockTests
{
    [Fact]
    public async Task Curfew_GameQueuedBeforeTheHour_IsNotARuleBreak()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        // Queued three hours ago; the curfew is the hour after it started, which has
        // already passed by now (unless the clock wrapped past midnight).
        var start = DateTimeOffset.Now.AddHours(-3);
        await new RulesRepository(scope.ConnectionFactory).CreateAsync("Curfew", ruleType: "no_play_after",
            conditionValue: (start.LocalDateTime.Hour + 1).ToString());

        await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7601, start)));

        Assert.False(await RuleBrokenAsync(scope, 7601));
    }

    [Fact]
    public async Task Curfew_GameQueuedAfterTheHour_IsARuleBreak()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var start = DateTimeOffset.Now.AddMinutes(-40);
        await new RulesRepository(scope.ConnectionFactory).CreateAsync("Curfew", ruleType: "no_play_after",
            conditionValue: start.LocalDateTime.Hour.ToString());

        await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7602, start)));

        Assert.True(await RuleBrokenAsync(scope, 7602));
    }

    [Fact]
    public async Task CheckViolations_AsOf_JudgesTheGivenMoment()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        await rules.CreateAsync("Curfew", ruleType: "no_play_after", conditionValue: "23");
        var day = DateTime.Today;

        var before = await rules.CheckViolationsAsync([], asOf: new DateTimeOffset(day.AddHours(22).AddMinutes(50)));
        var after = await rules.CheckViolationsAsync([], asOf: new DateTimeOffset(day.AddHours(23).AddMinutes(10)));

        Assert.False(Assert.Single(before).Violated);
        Assert.True(Assert.Single(after).Violated);
    }

    [Fact]
    public async Task LossStreak_LaterLossesThatDay_DoNotArmTheCooldownForAnEarlierGame()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await new RulesRepository(scope.ConnectionFactory).CreateAsync("Cooldown", ruleType: "loss_streak",
            conditionValue: "2:60");
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12));
        await SeedSessionGameAsync(scope, 7611, win: false, noon.AddMinutes(60));
        await SeedSessionGameAsync(scope, 7612, win: false, noon.AddMinutes(80));

        // Reconciled after both losses were logged, but queued before either of them.
        await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7613, noon)));

        Assert.False(await RuleBrokenAsync(scope, 7613));
    }

    [Fact]
    public async Task LossStreak_EarlierLossesThatDay_ArmTheCooldown()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await new RulesRepository(scope.ConnectionFactory).CreateAsync("Cooldown", ruleType: "loss_streak",
            conditionValue: "2:60");
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12));
        await SeedSessionGameAsync(scope, 7621, win: false, noon.AddMinutes(-60));
        await SeedSessionGameAsync(scope, 7622, win: false, noon.AddMinutes(-40));

        // Queued 40 minutes after the loss that armed a 60 minute cooldown.
        await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7623, noon)));

        Assert.True(await RuleBrokenAsync(scope, 7623));
    }

    [Fact]
    public async Task MaxGames_AnOlderGameIsJudgedAgainstItsOwnDay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await new RulesRepository(scope.ConnectionFactory).CreateAsync("Two a day", ruleType: "max_games",
            conditionValue: "2");
        var yesterdayNoon = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12));
        await SeedSessionGameAsync(scope, 7631, win: true, yesterdayNoon.AddMinutes(-90));
        await SeedSessionGameAsync(scope, 7632, win: true, yesterdayNoon.AddMinutes(-45));
        await SeedSessionGameAsync(scope, 7633, win: true, yesterdayNoon.AddMinutes(45));

        // A missed game from yesterday, ingested today: the third game that day.
        await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7634, yesterdayNoon)));

        Assert.True(await RuleBrokenAsync(scope, 7634));
    }

    [Fact]
    public async Task CheckViolations_AsOf_IgnoresGamesFromThatMomentOn()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        await rules.CreateAsync("Cooldown", ruleType: "loss_streak", conditionValue: "2:60");
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12));
        IReadOnlyList<RuleCheckGame> later =
        [
            new(1, false, "Ahri", noon.AddMinutes(10).ToUnixTimeSeconds()),
            new(2, false, "Ahri", noon.AddMinutes(20).ToUnixTimeSeconds()),
        ];

        var atNoon = await rules.CheckViolationsAsync(later, asOf: noon);
        var afterBoth = await rules.CheckViolationsAsync(later, asOf: noon.AddMinutes(30));

        Assert.False(Assert.Single(atNoon).Violated);
        Assert.True(Assert.Single(afterBoth).Violated);
    }

    private static GameStats Game(long id, DateTimeOffset start)
    {
        var stats = TestGameStatsFactory.Create(id, timestamp: start.ToUnixTimeSeconds());
        stats.EndedInEarlySurrender = false;
        stats.EarlySurrenderSource = "eog";
        return stats;
    }

    private static async Task SeedSessionGameAsync(TestDatabaseScope scope, long gameId, bool win, DateTimeOffset start)
    {
        using var conn = scope.OpenConnection();
        await RemakeTestKit.InsertGameAsync(conn, gameId, win, start);
        await RemakeTestKit.InsertSessionRowAsync(conn, start.LocalDateTime.ToString("yyyy-MM-dd"), gameId, win, at: start);
    }

    private static async Task<bool> RuleBrokenAsync(TestDatabaseScope scope, long gameId) =>
        await RemakeTestKit.CountAsync(scope, $"SELECT COALESCE(rule_broken, 0) FROM session_log WHERE game_id = {gameId}") != 0;

    private static GameService BuildService(TestDatabaseScope scope) => new(
        scope.ConnectionFactory, scope.Games, scope.SessionLog, new RulesRepository(scope.ConnectionFactory),
        scope.GameEvents, scope.DerivedEvents, new TestConfigService(), NullLogger<GameService>.Instance);
}
