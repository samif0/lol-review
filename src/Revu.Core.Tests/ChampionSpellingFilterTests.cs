using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// Champion filters and gates match every spelling of a champion: history holds both
/// "Kai'Sa" and "Kaisa", and champ select reports one while the rows say the other.
/// </summary>
public sealed class ChampionSpellingFilterTests
{
    [Fact]
    public async Task RecentGames_FindTheOtherSpelling()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8501, champion: "Kaisa", win: false));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8502, champion: "Kai'Sa", win: true));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8503, champion: "Ahri", win: false));

        var recent = await scope.Games.GetRecentAsync(limit: 5, champion: "Kai'Sa");

        Assert.Equal([8501L, 8502L], recent.Select(g => g.GameId).Order());
        Assert.Equal(2, await scope.Games.GetRecentCountAsync(champion: "kaisa"));
    }

    [Fact]
    public async Task ObjectiveAndPromptChampionGates_PassTheOtherSpelling()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gated = await scope.Objectives.CreateWithPhasesAsync(
            "Space the dash", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var other = await scope.Objectives.CreateWithPhasesAsync(
            "Yasuo only", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        // Saved before champion names were canonicalized.
        await scope.Objectives.SetChampionsForObjectiveAsync(gated, ["Kaisa"]);
        await scope.Objectives.SetChampionsForObjectiveAsync(other, ["Yasuo"]);
        await scope.Prompts.CreatePromptAsync(gated, ObjectivePhases.PreGame, "Where is the enemy jungler?", 0);

        var objectives = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PreGame, "Kai'Sa");
        var prompts = await scope.Prompts.GetActivePromptsForPhaseAsync(ObjectivePhases.PreGame, "Kai'Sa");

        Assert.Equal(gated, Assert.Single(objectives).Id);
        Assert.Equal(gated, Assert.Single(prompts).ObjectiveId);
    }
}
