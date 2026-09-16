using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class BookmarkPracticeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaggedSave_MarksOnlyThisGamesObjectivePracticed_AndPreservesReviewNotes(bool clip)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(9201);
        var otherGame = await scope.SeedGameAsync(9202);
        var objectiveId = await scope.Objectives.CreateAsync("Tempo");
        var otherObjectiveId = await scope.Objectives.CreateAsync("Positioning");
        await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, false, "Committed execution note");
        await scope.Objectives.RecordGameAsync(game.GameId, otherObjectiveId, false, "Another objective note");
        await scope.Objectives.RecordGameAsync(otherGame.GameId, objectiveId, false, "Another VOD note");
        await scope.ReviewDrafts.UpsertAsync(new ReviewDraft
        {
            GameId = game.GameId,
            ReviewNotes = "Unfinished debrief",
            WentWell = "Good resets",
            MentalRating = 8,
            SelectedTagIdsJson = "[1,2]",
            ObjectiveAssessmentsJson = JsonSerializer.Serialize(new[]
            {
                new SaveObjectivePracticeRequest(objectiveId, false, "Newer draft execution note"),
                new SaveObjectivePracticeRequest(otherObjectiveId, false, "Another draft note"),
            }),
        });
        var before = await scope.Objectives.GetAsync(objectiveId);

        // Repeat saves to ensure practice credit is per game, not per bookmark.
        for (var i = 0; i < 2; i++)
        {
            if (clip)
                await ClipPersistence.PersistAsync(scope.Vod, scope.Objectives, scope.Evidence,
                    game.GameId, 300 + i * 50, 330 + i * 50, $"synthetic-clip-{i}.mp4",
                    "Tempo decision", "", objectiveId, reviewDrafts: scope.ReviewDrafts);
            else
                await BookmarkPersistence.AddAsync(scope.Vod, scope.Objectives, game.GameId,
                    300 + i * 50, "Tempo decision", objectiveId: objectiveId, reviewDrafts: scope.ReviewDrafts);
        }

        var practice = Assert.Single(await scope.Objectives.GetGameObjectivesAsync(game.GameId),
            row => row.ObjectiveId == objectiveId);
        Assert.True(practice.Practiced);
        Assert.Equal("Committed execution note", practice.ExecutionNote);
        Assert.False(Assert.Single(await scope.Objectives.GetGameObjectivesAsync(game.GameId),
            row => row.ObjectiveId == otherObjectiveId).Practiced);
        Assert.False(Assert.Single(await scope.Objectives.GetGameObjectivesAsync(otherGame.GameId)).Practiced);
        var after = await scope.Objectives.GetAsync(objectiveId);
        // Clips separately earn two evidence points each; practice still earns
        // only two points for this game across both saves.
        Assert.Equal(before!.Score + 2 + (clip ? 4 : 0), after!.Score);
        Assert.Equal(before.GameCount, after.GameCount);

        var draft = await scope.ReviewDrafts.GetAsync(game.GameId);
        Assert.NotNull(draft);
        Assert.Equal("Unfinished debrief", draft.ReviewNotes);
        Assert.Equal("Good resets", draft.WentWell);
        Assert.Equal(8, draft.MentalRating);
        Assert.Equal("[1,2]", draft.SelectedTagIdsJson);
        var assessments = JsonSerializer.Deserialize<List<SaveObjectivePracticeRequest>>(draft.ObjectiveAssessmentsJson)!;
        var draftedPractice = Assert.Single(assessments, row => row.ObjectiveId == objectiveId);
        Assert.True(draftedPractice.Practiced);
        Assert.Equal("Newer draft execution note", draftedPractice.ExecutionNote);
        Assert.Equal(new SaveObjectivePracticeRequest(otherObjectiveId, false, "Another draft note"),
            Assert.Single(assessments, row => row.ObjectiveId == otherObjectiveId));

        var builder = new ReviewSnapshotBuilder(
            scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog,
            scope.Evidence, new GameEventsRepository(scope.ConnectionFactory),
            scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod,
            scope.Config, scope.ReviewDrafts, NullLogger<ReviewSnapshotBuilder>.Instance);
        var snapshot = await builder.BuildAsync(game.GameId);
        var visiblePractice = Assert.Single(snapshot.Subject!.Objectives, row => row.Id == objectiveId);
        Assert.True(visiblePractice.Practiced);
        Assert.Equal("Newer draft execution note", visiblePractice.ExecutionNote);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntaggedSave_DoesNotCreatePracticeOrDraft(bool clip)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(9203);
        await scope.Objectives.CreateAsync("Tempo");

        if (clip)
            await ClipPersistence.PersistAsync(scope.Vod, scope.Objectives, scope.Evidence,
                game.GameId, 300, 330, "synthetic-clip.mp4", "Decision", "", null,
                reviewDrafts: scope.ReviewDrafts);
        else
            await BookmarkPersistence.AddAsync(scope.Vod, scope.Objectives, game.GameId,
                300, "Decision", reviewDrafts: scope.ReviewDrafts);

        Assert.Empty(await scope.Objectives.GetGameObjectivesAsync(game.GameId));
        Assert.Null(await scope.ReviewDrafts.GetAsync(game.GameId));
        Assert.Single(await scope.Vod.GetBookmarksAsync(game.GameId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetaggingCreditsTheAttachedObjective_DetachingKeepsPractice(bool clip)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(9204);
        var firstObjective = await scope.Objectives.CreateAsync("Tempo");
        var secondObjective = await scope.Objectives.CreateAsync("Positioning");
        await scope.ReviewDrafts.UpsertAsync(new ReviewDraft
        {
            GameId = game.GameId,
            ReviewNotes = "Still writing",
            ObjectiveAssessmentsJson = "[]",
        });
        var bookmarkId = await BookmarkPersistence.AddAsync(scope.Vod, scope.Objectives,
            game.GameId, 400, clipPath: clip ? "synthetic-clip.mp4" : "");

        await BookmarkPersistence.SetTagAsync(scope.Vod, scope.Objectives, bookmarkId,
            firstObjective, reviewDrafts: scope.ReviewDrafts);
        await BookmarkPersistence.SetTagAsync(scope.Vod, scope.Objectives, bookmarkId,
            secondObjective, reviewDrafts: scope.ReviewDrafts);
        await BookmarkPersistence.SetTagAsync(scope.Vod, scope.Objectives, bookmarkId,
            null, reviewDrafts: scope.ReviewDrafts);

        Assert.Null(Assert.Single(await scope.Vod.GetBookmarksAsync(game.GameId)).ObjectiveId);
        var practices = await scope.Objectives.GetGameObjectivesAsync(game.GameId);
        Assert.Equal(2, practices.Count);
        Assert.All(practices, row => Assert.True(row.Practiced));
        Assert.All(practices, row => Assert.Equal("", row.ExecutionNote));
        var draft = await scope.ReviewDrafts.GetAsync(game.GameId);
        Assert.Equal("Still writing", draft!.ReviewNotes);
        var draftedPractices = JsonSerializer.Deserialize<List<SaveObjectivePracticeRequest>>(draft.ObjectiveAssessmentsJson)!;
        Assert.Equal(2, draftedPractices.Count);
        Assert.All(draftedPractices, row => Assert.True(row.Practiced));
    }

    [Fact]
    public async Task MissingBookmark_DoesNotCreditPractice()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(9205);
        var objectiveId = await scope.Objectives.CreateAsync("Tempo");

        await BookmarkPersistence.SetTagAsync(scope.Vod, scope.Objectives, long.MaxValue,
            objectiveId, reviewDrafts: scope.ReviewDrafts);

        Assert.Empty(await scope.Objectives.GetGameObjectivesAsync(game.GameId));
        Assert.Null(await scope.ReviewDrafts.GetAsync(game.GameId));
    }
}
