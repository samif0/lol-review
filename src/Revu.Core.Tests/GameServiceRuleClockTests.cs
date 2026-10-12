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

    private static GameStats Game(long id, DateTimeOffset start)
    {
        var stats = TestGameStatsFactory.Create(id, timestamp: start.ToUnixTimeSeconds());
        stats.EndedInEarlySurrender = false;
        stats.EarlySurrenderSource = "eog";
        return stats;
    }

    private static async Task<bool> RuleBrokenAsync(TestDatabaseScope scope, long gameId) =>
        await RemakeTestKit.CountAsync(scope, $"SELECT COALESCE(rule_broken, 0) FROM session_log WHERE game_id = {gameId}") != 0;

    private static GameService BuildService(TestDatabaseScope scope) => new(
        scope.ConnectionFactory, scope.Games, scope.SessionLog, new RulesRepository(scope.ConnectionFactory),
        scope.GameEvents, scope.DerivedEvents, new TestConfigService(), NullLogger<GameService>.Instance);
}
