using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// P2c (digest 2026-06-14): rules carry an optional player-authored replacement
/// plan ("then I will…"). It must round-trip through create/read/update and
/// default to empty for rules created without one. Display-only — these tests
/// only assert persistence, since nothing scores or flags the plan.
/// </summary>
public sealed class RulesRepositoryReplacementPlanTests
{
    [Fact]
    public async Task CreateAsync_PersistsReplacementPlan_AndReadsItBack()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        const string plan = "close the client and walk for 10 minutes before I even think about requeue.";
        var id = await rules.CreateAsync(
            "Stop after 2 losses",
            ruleType: "loss_streak",
            conditionValue: "2:120",
            replacementPlan: plan);

        var record = await rules.GetAsync(id);
        Assert.NotNull(record);
        Assert.Equal(plan, record!.ReplacementPlan);
    }

    [Fact]
    public async Task CreateAsync_WithoutPlan_DefaultsToEmpty()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var id = await rules.CreateAsync("Max 3 games", ruleType: "max_games", conditionValue: "3");

        var record = await rules.GetAsync(id);
        Assert.NotNull(record);
        Assert.Equal("", record!.ReplacementPlan);
    }

    [Fact]
    public async Task UpdateAsync_CanSetAndClearReplacementPlan()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var id = await rules.CreateAsync("Min mental 5", ruleType: "min_mental", conditionValue: "5");

        // Add a plan via update.
        await rules.UpdateAsync(id, "Min mental 5", "", "min_mental", "5",
            replacementPlan: "take a 5-minute break and a glass of water.");
        var afterSet = await rules.GetAsync(id);
        Assert.Equal("take a 5-minute break and a glass of water.", afterSet!.ReplacementPlan);

        // Clear it again (empty string).
        await rules.UpdateAsync(id, "Min mental 5", "", "min_mental", "5", replacementPlan: "");
        var afterClear = await rules.GetAsync(id);
        Assert.Equal("", afterClear!.ReplacementPlan);
    }

    [Fact]
    public async Task GetActiveAsync_IncludesReplacementPlan()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        const string plan = "alt-f4 and go outside.";
        await rules.CreateAsync("Stop after 3 losses", ruleType: "loss_streak", conditionValue: "3",
            replacementPlan: plan);

        var active = await rules.GetActiveAsync();
        var rule = Assert.Single(active);
        Assert.Equal(plan, rule.ReplacementPlan);
    }
}
