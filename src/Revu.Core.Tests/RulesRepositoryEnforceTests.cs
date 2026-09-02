using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>v3.7 (schema v14): rules.enforce round-trips, defaults to false for
/// every rule (existing and new), and never leaks into other columns.</summary>
public sealed class RulesRepositoryEnforceTests
{
    [Fact]
    public async Task CreateAsync_DefaultsEnforceToFalse()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var id = await rules.CreateAsync("Max 6 games", ruleType: "max_games", conditionValue: "6");

        var record = await rules.GetAsync(id);
        Assert.NotNull(record);
        Assert.False(record!.Enforce);
    }

    [Fact]
    public async Task SetEnforceAsync_RoundTripsThroughEveryReader()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var id = await rules.CreateAsync("Max 6 games", ruleType: "max_games", conditionValue: "6", replacementPlan: "walk");
        await rules.SetEnforceAsync(id, true);

        Assert.True((await rules.GetAsync(id))!.Enforce);
        Assert.True(Assert.Single(await rules.GetActiveAsync()).Enforce);
        Assert.True(Assert.Single(await rules.GetAllAsync()).Enforce);
        // The active-rule list feeds CheckViolationsAsync → the enforcer sees it.
        var violation = Assert.Single(await rules.CheckViolationsAsync([]));
        Assert.True(violation.Rule.Enforce);

        await rules.SetEnforceAsync(id, false);
        var after = await rules.GetAsync(id);
        Assert.False(after!.Enforce);
        Assert.Equal("walk", after.ReplacementPlan); // untouched
        Assert.True(after.IsActive);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotResetEnforce()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);

        var id = await rules.CreateAsync("Stop after 2", ruleType: "loss_streak", conditionValue: "2:120");
        await rules.SetEnforceAsync(id, true);
        await rules.UpdateAsync(id, "Stop after 2 losses", "reset", "loss_streak", "2:90", replacementPlan: "tea");

        var record = await rules.GetAsync(id);
        Assert.True(record!.Enforce);
        Assert.Equal("2:90", record.ConditionValue);
    }
}
