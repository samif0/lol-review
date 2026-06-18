using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// v3.0.15: event-token gating on objectives (objective_event_types). Round-trips
/// the tied tokens and the active-objective token map the VOD timeline reads.
/// </summary>
public sealed class ObjectivesRepositoryEventTokensTests
{
    [Fact]
    public async Task SetEventTokensForObjectiveAsync_PersistsAndIsReadable()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Track flashes", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetEventTokensForObjectiveAsync(id, new[] { "SPELL_FLASH", "DRAGON", "TEAMFIGHT" });

        var tokens = await scope.Objectives.GetEventTokensForObjectiveAsync(id);
        Assert.Equal(
            new[] { "DRAGON", "SPELL_FLASH", "TEAMFIGHT" }.OrderBy(t => t),
            tokens.OrderBy(t => t));
    }

    [Fact]
    public async Task SetEventTokensForObjectiveAsync_DropsUnknownTokens()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetEventTokensForObjectiveAsync(id, new[] { "KILL", "NOT_A_TOKEN", "" });

        var tokens = await scope.Objectives.GetEventTokensForObjectiveAsync(id);
        Assert.Equal(new[] { "KILL" }, tokens);
    }

    [Fact]
    public async Task GetActiveObjectiveEventTokensAsync_ReturnsTiesForActiveObjectives()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Objective control", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetEventTokensForObjectiveAsync(id, new[] { "DRAGON", "BARON" });

        var ties = await scope.Objectives.GetActiveObjectiveEventTokensAsync();
        Assert.Equal(
            new[] { "BARON", "DRAGON" }.OrderBy(t => t),
            ties.Select(t => t.Token).OrderBy(t => t));
        Assert.All(ties, t => Assert.Equal(id, t.ObjectiveId));
    }

    [Fact]
    public async Task DeleteAsync_ClearsEventTokens()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetEventTokensForObjectiveAsync(id, new[] { "KILL", "SPELL_SMITE" });

        await scope.Objectives.DeleteAsync(id);

        var tokens = await scope.Objectives.GetEventTokensForObjectiveAsync(id);
        Assert.Empty(tokens);
    }
}
