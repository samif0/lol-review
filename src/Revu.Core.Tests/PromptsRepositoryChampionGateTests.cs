using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// v2.15.0: PromptsRepository.GetActivePromptsForPhaseAsync honors the
/// champion filter on the parent objective — same rule as the objectives
/// query (empty champion set = apply to all champions).
/// </summary>
public sealed class PromptsRepositoryChampionGateTests
{
    [Fact]
    public async Task GetActivePromptsForPhaseAsync_NoChampion_ReturnsAll()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var open = await scope.Objectives.CreateWithPhasesAsync(
            "Open", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var scoped = await scope.Objectives.CreateWithPhasesAsync(
            "Yasuo-only", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetChampionsForObjectiveAsync(scoped, new[] { "Yasuo" });
        await scope.Prompts.CreatePromptAsync(open,   ObjectivePhases.PreGame, "open q",   0);
        await scope.Prompts.CreatePromptAsync(scoped, ObjectivePhases.PreGame, "yasuo q",  0);

        var all = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame, championName: null);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_MatchingChampion_OnlyMatchingOrOpenPass()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var open = await scope.Objectives.CreateWithPhasesAsync(
            "Open", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var yasuoOnly = await scope.Objectives.CreateWithPhasesAsync(
            "Yasuo", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var ahriOnly = await scope.Objectives.CreateWithPhasesAsync(
            "Ahri", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetChampionsForObjectiveAsync(yasuoOnly, new[] { "Yasuo" });
        await scope.Objectives.SetChampionsForObjectiveAsync(ahriOnly, new[] { "Ahri" });

        await scope.Prompts.CreatePromptAsync(open,      ObjectivePhases.PreGame, "open q",  0);
        await scope.Prompts.CreatePromptAsync(yasuoOnly, ObjectivePhases.PreGame, "yasuo q", 0);
        await scope.Prompts.CreatePromptAsync(ahriOnly,  ObjectivePhases.PreGame, "ahri q",  0);

        var forYasuo = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame, "Yasuo");

        Assert.Equal(2, forYasuo.Count);
        var titles = forYasuo.Select(p => p.ObjectiveTitle).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "Open", "Yasuo" }, titles);
    }

    [Fact]
    public async Task GetActivePromptsForPhaseAsync_ChampionMismatch_HidesAllScoped()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var yasuoOnly = await scope.Objectives.CreateWithPhasesAsync(
            "Yasuo", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetChampionsForObjectiveAsync(yasuoOnly, new[] { "Yasuo" });
        await scope.Prompts.CreatePromptAsync(yasuoOnly, ObjectivePhases.PreGame, "yasuo q", 0);

        // Playing Orianna → objective filters out entirely, no prompts render.
        var forOrianna = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame, "Orianna");
        Assert.Empty(forOrianna);
    }
}
