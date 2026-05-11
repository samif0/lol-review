using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class ReviewExportServiceTests
{
    [Fact]
    public async Task ExportGameAsync_IncludesHumanReadableReviewData()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 8_001;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Kai'Sa", win: false));
        await scope.Games.UpdateEnemyLanerAsync(gameId, "Smolder");
        await scope.Games.UpdateReviewAsync(gameId, new GameReview
        {
            Rating = 7,
            Notes = "Got punished for mid-wave reset.",
            WentWell = "Tracked jungle before dragon.",
            Mistakes = "Used E before threat was down.",
            FocusNext = "Hold E until Smolder W is committed.",
            SpottedProblems = "Late reset timing.",
            Attribution = "My play",
            PersonalContribution = "Could have pinged support earlier."
        });

        var tagId = await scope.ConceptTags.CreateAsync("reset timing", "negative", "#D38C90");
        await scope.ConceptTags.SetForGameAsync(gameId, [tagId]);

        var objectiveId = await scope.Objectives.CreateAsync(
            "Crash before recall",
            completionCriteria: "Wave hits tower before base");
        await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true, executionNote: "Missed one ranged minion.");
        var promptId = await scope.Prompts.CreatePromptAsync(objectiveId, ObjectivePhases.PostGame, "Did you crash?", 0);
        await scope.Prompts.SaveAnswerAsync(promptId, gameId, "No, I recalled too early.");

        await scope.MatchupNotes.UpsertForGameAsync(gameId, "Kai'Sa", "Smolder", "Respect level 6 poke.");
        await scope.Vod.LinkVodAsync(gameId, @"C:\vods\kaisa-loss.mp4", 1234, 1820);
        await scope.Vod.AddBookmarkAsync(
            gameId,
            615,
            "Bad recall setup",
            clipStartSeconds: 600,
            clipEndSeconds: 630,
            clipPath: @"C:\clips\recall.mp4",
            objectiveId: objectiveId,
            quality: "bad",
            promptId: promptId);

        var service = CreateService(scope);
        var markdown = await service.ExportGameAsync(gameId);

        Assert.NotNull(markdown);
        Assert.Contains("# Revu Game Review Export", markdown);
        Assert.Contains("Kai'Sa", markdown);
        Assert.Contains("Smolder", markdown);
        Assert.Contains("Got punished for mid-wave reset.", markdown);
        Assert.Contains("reset timing", markdown);
        Assert.Contains("Crash before recall", markdown);
        Assert.Contains("No, I recalled too early.", markdown);
        Assert.Contains(@"C:\vods\kaisa-loss.mp4", markdown);
        Assert.Contains("Bad recall setup", markdown);
    }

    [Fact]
    public async Task ExportAllAsync_IncludesEveryRecentGame()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await scope.Games.SaveAsync(TestGameStatsFactory.Create(9_001, champion: "Ahri", timestamp: 9_001));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(9_002, champion: "Lux", timestamp: 9_002));

        var service = CreateService(scope);
        var markdown = await service.ExportAllAsync();

        Assert.Contains("Games exported:** 2", markdown);
        Assert.Contains("Ahri", markdown);
        Assert.Contains("Lux", markdown);
    }

    private static ReviewExportService CreateService(TestDatabaseScope scope) =>
        new(
            scope.Games,
            scope.Objectives,
            scope.ConceptTags,
            scope.Prompts,
            scope.Vod,
            scope.MatchupNotes);
}
