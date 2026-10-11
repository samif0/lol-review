using Revu.Core.Data.Repositories;
using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>
/// 3.14.1: the behavioral rule evidence (trigger games, the adherence streak) never
/// sees a remake. Past remakes stop counting; a remake-only day is a non-play day.
/// </summary>
public sealed class RulesRepositoryRemakeEvidenceTests
{
    [Fact]
    public async Task MaxGames_GameAfterARemake_DoesNotTrigger()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        var day = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 9101, win: true, day);
            await InsertGameAsync(conn, 9102, win: false, day.AddMinutes(40));
            await InsertGameAsync(conn, 9103, win: false, day.AddMinutes(80), duration: 386);
            await InsertGameAsync(conn, 9104, win: true, day.AddMinutes(120));
        }
        await MarkAsync(scope, 9103);
        var maxGamesId = await rules.CreateAsync("Max 3 games", ruleType: "max_games", conditionValue: "3");

        var evidence = (await rules.GetRuleEvidenceAsync(await rules.GetAllAsync()))[maxGamesId];

        Assert.Equal(0, evidence.TriggerGames);
        Assert.Equal(3, evidence.BaselineGames);
    }

    [Fact]
    public async Task LossStreak_RemakeBetweenLosses_DoesNotExtendTheStreak()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        var day = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        using (var conn = scope.OpenConnection())
        {
            await InsertGameAsync(conn, 9201, win: false, day);
            await InsertGameAsync(conn, 9202, win: false, day.AddMinutes(20), duration: 300);
            await InsertGameAsync(conn, 9203, win: false, day.AddMinutes(40));
            // Would trip a 3-loss streak if the remake counted as the 2nd loss.
            await InsertGameAsync(conn, 9204, win: true, day.AddMinutes(60));
        }
        await MarkAsync(scope, 9202);
        var lossStreakId = await rules.CreateAsync("Stop after 3 losses", ruleType: "loss_streak", conditionValue: "3:120");

        var evidence = (await rules.GetRuleEvidenceAsync(await rules.GetAllAsync()))[lossStreakId];

        Assert.Equal(0, evidence.TriggerGames);
    }

    [Fact]
    public async Task AdherenceStreak_RemakeOnlyDayIsANonPlayDay()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        var twoDaysAgo = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(12));
        var yesterday = twoDaysAgo.AddDays(1);
        using (var conn = scope.OpenConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO rules (name, rule_type, condition_value, is_active, created_at) VALUES ('Max 1', 'max_games', '1', 1, @at)";
                cmd.Parameters.AddWithValue("@at", twoDaysAgo.AddDays(-5).ToUnixTimeSeconds());
                await cmd.ExecuteNonQueryAsync();
            }
            await InsertGameAsync(conn, 9301, win: true, twoDaysAgo);
            await InsertGameAsync(conn, 9302, win: false, yesterday, duration: 300);
        }

        // Counted, the remake would be a second play-day (2); excluded, one clean day remains.
        Assert.Equal(2, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
        await MarkAsync(scope, 9302);
        Assert.Equal(1, await rules.GetBehavioralAdherenceStreakAsync("2000-01-01"));
    }
}
