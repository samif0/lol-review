using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// 3.14.1: a remake (League's early-surrender flag) is saved and reviewable but never
/// counted, so it can never trip a "max games" rule. An unflagged short game is still
/// skipped, exactly as before.
/// </summary>
public sealed class GameServiceRemakeTests
{
    private static GameService BuildService(TestDatabaseScope scope) => new(
        scope.Games, scope.SessionLog, new RulesRepository(scope.ConnectionFactory),
        scope.GameEvents, scope.DerivedEvents, new TestConfigService(), NullLogger<GameService>.Instance);

    private static long _clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;

    private static GameStats Game(long id, int duration = 1800, bool? flag = false, bool win = false)
    {
        var stats = TestGameStatsFactory.Create(id, win: win, timestamp: Interlocked.Increment(ref _clock), durationSeconds: duration);
        stats.EndedInEarlySurrender = flag;
        stats.EarlySurrenderSource = flag is null ? null : "eog";
        return stats;
    }

    private static async Task<bool> RuleBrokenAsync(TestDatabaseScope scope, long gameId) =>
        await RemakeTestKit.CountAsync(scope, $"SELECT COALESCE(rule_broken, 0) FROM session_log WHERE game_id = {gameId}") != 0;

    [Fact]
    public async Task FlaggedGame_IsSavedMarkedAndNotCounted()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = BuildService(scope);

        var saved = await svc.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7101, duration: 386, flag: true)));

        Assert.Equal(7101, saved);
        var verdict = await scope.Games.GetRemakeVerdictAsync(7101);
        Assert.NotNull(verdict);
        Assert.True(verdict!.IsRemake);
        Assert.Equal("eog", verdict.Source);
        Assert.False(await RuleBrokenAsync(scope, 7101));
        Assert.Equal(1, await RemakeTestKit.CountAsync(scope, "SELECT COUNT(*) FROM session_log WHERE game_id = 7101"));
        Assert.DoesNotContain(await scope.SessionLog.GetTodayAsync(), e => e.GameId == 7101);
        Assert.True((await scope.Games.GetAsync(7101))!.IsRemake);
    }

    [Fact]
    public async Task FlaggedVeryShortGame_IsSavedNotSkipped()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var saved = await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7102, duration: 120, flag: true)));

        Assert.Equal(7102, saved);
        Assert.NotNull(await scope.Games.GetAsync(7102));
    }

    [Fact]
    public async Task UnflaggedShortGame_IsStillSkipped()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        Assert.Null(await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7103, duration: 120, flag: null))));
        Assert.Null(await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7104, duration: 120, flag: false))));
        Assert.Null(await scope.Games.GetAsync(7103));
        Assert.Null(await scope.Games.GetAsync(7104));
    }

    [Fact]
    public async Task MaxGamesThree_RemakeDoesNotCount()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await new RulesRepository(scope.ConnectionFactory).CreateAsync("Three games", ruleType: "max_games", conditionValue: "3");
        var svc = BuildService(scope);

        await svc.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7201)));
        await svc.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7202)));
        await svc.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7203, duration: 386, flag: true)));
        await svc.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7204)));

        Assert.False(await RuleBrokenAsync(scope, 7203));
        Assert.False(await RuleBrokenAsync(scope, 7204));
        Assert.Equal(3, (await scope.SessionLog.GetTodayAsync()).Count);

        await svc.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7205)));
        Assert.True(await RuleBrokenAsync(scope, 7205));
    }

    [Fact]
    public async Task ExplicitFalseFlag_RecordsANegativeVerdict()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await BuildService(scope).ProcessGameEndAsync(new ProcessGameEndRequest(Game(7301, flag: false)));

        var verdict = await scope.Games.GetRemakeVerdictAsync(7301);
        Assert.NotNull(verdict);
        Assert.False(verdict!.IsRemake);
        Assert.Equal("eog", verdict.Source);
        Assert.False((await scope.Games.GetAsync(7301))!.IsRemake);
    }

    [Fact]
    public async Task Lifecycle_DoesNotRecordObjectivesForARemake()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river");
        var lifecycle = new GameLifecycleWorkflowService(
            BuildService(scope), scope.MissedGameDecisions, scope.Objectives,
            new NullCoachSidecarNotifier(), NullLogger<GameLifecycleWorkflowService>.Instance);

        await lifecycle.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7401, duration: 386, flag: true),
            PreGamePracticedObjectiveIds: [objectiveId]));
        await lifecycle.ProcessGameEndAsync(new ProcessGameEndRequest(Game(7402),
            PreGamePracticedObjectiveIds: [objectiveId]));

        Assert.Equal(0, await RemakeTestKit.CountAsync(scope, "SELECT COUNT(*) FROM game_objectives WHERE game_id = 7401"));
        Assert.Equal(1, await RemakeTestKit.CountAsync(scope, "SELECT COUNT(*) FROM game_objectives WHERE game_id = 7402"));
    }
}
