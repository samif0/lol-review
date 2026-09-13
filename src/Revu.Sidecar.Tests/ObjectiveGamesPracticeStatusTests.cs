using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class ObjectiveGamesPracticeStatusTests
{
    [Theory]
    [InlineData(false, "Objective not practiced")]
    [InlineData(true, "Objective practiced")]
    public async Task SavedReview_ShowsObjectivePracticeWithoutCallingReviewSkipped(
        bool practiced, string expectedLabel)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8601, champion: "Ahri");
        var objectiveId = await scope.Objectives.CreateAsync("Track cooldowns", phase: ObjectivePhases.InGame);
        const string reviewNotes = "Wait for their key spell before trading.";
        var snapshot = new ReviewSnapshot(
            MentalRating: 6,
            WentWell: "",
            Mistakes: "",
            FocusNext: "",
            ReviewNotes: reviewNotes,
            ImprovementNote: "",
            Attribution: "",
            MentalHandled: "",
            SpottedProblems: "",
            OutsideControl: "",
            WithinControl: "",
            PersonalContribution: "",
            EnemyLaner: "",
            MatchupNote: "",
            SelectedTagIds: [],
            ObjectivePractices: [new SaveObjectivePracticeRequest(objectiveId, practiced, "")]);

        var result = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            game.GameId, game.ChampionName, game.Win, RequireReviewNotes: true, Snapshot: snapshot));
        Assert.True(result.Success, result.ErrorMessage);
        var savedReview = await scope.Games.GetAsync(game.GameId);
        Assert.NotNull(savedReview);
        Assert.True(savedReview.Rating > 0);
        Assert.Equal(reviewNotes, savedReview.ReviewNotes);

        var builder = new ObjectiveGamesSnapshotBuilder(
            scope.Objectives, scope.Evidence, scope.Vod,
            NullLogger<ObjectiveGamesSnapshotBuilder>.Instance);
        var page = await builder.BuildAsync(objectiveId);
        var row = Assert.Single(page.Games);

        Assert.Equal(game.GameId, row.GameId);
        Assert.Equal(practiced, row.Practiced);
        Assert.Equal(expectedLabel, row.PracticedText);
        Assert.DoesNotContain("Skipped", row.PracticedText);
        Assert.Equal(practiced ? 1 : 0, page.PracticedCount);

        // Displaying the practice badge must not rewrite the saved review or
        // award practice merely because the player completed their review.
        var afterDisplay = await scope.Games.GetAsync(game.GameId);
        Assert.NotNull(afterDisplay);
        Assert.Equal(savedReview.Rating, afterDisplay.Rating);
        Assert.Equal(reviewNotes, afterDisplay.ReviewNotes);
        var practice = Assert.Single(await scope.Objectives.GetGameObjectivesAsync(game.GameId));
        Assert.Equal(practiced, practice.Practiced);
    }
}
