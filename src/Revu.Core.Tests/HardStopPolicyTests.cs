using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.7 (hard stop): the pure decision. The sidecar's enforcer is a thin wrapper
/// around this, so the interesting behavior — which rules may ever cancel a
/// queue, and when the countdown ends — is pinned here without a DB.
/// </summary>
public sealed class HardStopPolicyTests
{
    private static RuleRecord Rule(long id, string type, string condition, bool enforce = true, bool active = true) =>
        new(id, $"rule-{id}", "", type, condition, active, CreatedAt: 1, ReplacementPlan: "walk", Enforce: enforce);

    private static RuleCheckGame Game(long ts, bool win) => new(GameId: ts, Win: win, ChampionName: "Jinx", Timestamp: ts);

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 21, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 2, 21, 30, 0)));

    [Fact]
    public void Decide_ReturnsNull_WhenNothingTripped()
    {
        var violations = new[] { new RuleViolation(Rule(1, "max_games", "6"), Violated: false, Reason: "") };
        Assert.Null(HardStopPolicy.Decide(violations, new HashSet<long>(), [], Now));
    }

    [Fact]
    public void Decide_ReturnsNull_WhenTrippedRuleIsNotEnforced()
    {
        // The pre-v14 world: a trip is a label, never an action.
        var violations = new[] { new RuleViolation(Rule(1, "max_games", "6", enforce: false), true, "Already played 6/6 games today") };
        Assert.Null(HardStopPolicy.Decide(violations, new HashSet<long>(), [], Now));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("min_mental")]
    public void Decide_NeverEnforcesTypesWithoutAReplayableCondition(string type)
    {
        var violations = new[] { new RuleViolation(Rule(1, type, "4"), true, "tripped") };
        Assert.Null(HardStopPolicy.Decide(violations, new HashSet<long>(), [], Now));
        Assert.False(HardStopPolicy.CanEnforce(type));
    }

    [Fact]
    public void Decide_SkipsRulesOverriddenToday()
    {
        var violations = new[]
        {
            new RuleViolation(Rule(1, "max_games", "6"), true, "Already played 6/6 games today"),
            new RuleViolation(Rule(2, "no_play_after", "23"), true, "It's past 23:00"),
        };
        var decision = HardStopPolicy.Decide(violations, new HashSet<long> { 1 }, [], Now);
        Assert.NotNull(decision);
        Assert.Equal(2, decision!.Rule.Id);
        Assert.Equal("It's past 23:00", decision.Reason);
    }

    [Fact]
    public void Decide_SkipsInactiveRules()
    {
        var violations = new[] { new RuleViolation(Rule(1, "max_games", "6", active: false), true, "tripped") };
        Assert.Null(HardStopPolicy.Decide(violations, new HashSet<long>(), [], Now));
    }

    [Fact]
    public void Decide_MaxGames_UnlocksAtNextLocalMidnight()
    {
        var violations = new[] { new RuleViolation(Rule(1, "max_games", "6"), true, "Already played 6/6 games today") };
        var decision = HardStopPolicy.Decide(violations, new HashSet<long>(), [], Now);

        Assert.NotNull(decision);
        var unlock = DateTimeOffset.FromUnixTimeSeconds(decision!.UnlockAt!.Value).ToLocalTime();
        Assert.Equal(TimeSpan.Zero, unlock.TimeOfDay);
        Assert.Equal(Now.ToLocalTime().Date.AddDays(1), unlock.Date);
        Assert.True(decision.UnlockAt > Now.ToUnixTimeSeconds());
        Assert.True(decision.UnlockAt <= Now.ToUnixTimeSeconds() + 24 * 3600 + 3600); // DST slack
    }

    [Fact]
    public void Decide_LossStreakWithCooldown_UnlocksWhenTheArmingLossCoolsOff()
    {
        // Mirrors CheckViolationsAsync: the loss that first crossed the threshold
        // arms the cooldown; later losses do not re-arm it.
        var t0 = Now.ToUnixTimeSeconds() - 50 * 60;
        var games = new[]
        {
            Game(t0, win: true),
            Game(t0 + 10 * 60, win: false),  // loss 1
            Game(t0 + 20 * 60, win: false),  // loss 2 → arms the 2:120 cooldown
            Game(t0 + 30 * 60, win: false),  // loss 3, during cooldown
        };
        var rule = Rule(1, "loss_streak", "2:120");
        var violations = new[] { new RuleViolation(rule, true, "3 consecutive losses — cooldown ends in 1h 30m") };

        var decision = HardStopPolicy.Decide(violations, new HashSet<long>(), games, Now);

        Assert.NotNull(decision);
        Assert.Equal(t0 + 20 * 60 + 120 * 60, decision!.UnlockAt);
    }

    [Fact]
    public void Decide_LossStreakWithoutCooldown_UnlocksAtMidnight()
    {
        var t0 = Now.ToUnixTimeSeconds() - 30 * 60;
        var games = new[] { Game(t0, false), Game(t0 + 600, false) };
        var violations = new[] { new RuleViolation(Rule(1, "loss_streak", "2"), true, "2 consecutive losses") };

        var decision = HardStopPolicy.Decide(violations, new HashSet<long>(), games, Now);

        Assert.NotNull(decision);
        Assert.Equal(HardStopPolicy.NextLocalMidnight(Now), decision!.UnlockAt);
    }

    [Fact]
    public void StartOfLocalDay_IsMidnightBeforeNow()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(HardStopPolicy.StartOfLocalDay(Now)).ToLocalTime();
        Assert.Equal(TimeSpan.Zero, start.TimeOfDay);
        Assert.Equal(Now.ToLocalTime().Date, start.Date);
        Assert.True(HardStopPolicy.StartOfLocalDay(Now) <= Now.ToUnixTimeSeconds());
    }
}
