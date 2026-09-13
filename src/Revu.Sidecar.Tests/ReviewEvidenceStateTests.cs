using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class ReviewEvidenceStateTests
{
    [Fact]
    public async Task SavedRatingsAndAttachments_AgreeAcrossReviewAndObjectiveSnapshots_AfterRereading()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(gameId: 8801);
        var tempoId = await scope.Objectives.CreateAsync("Tempo");
        var positioningId = await scope.Objectives.CreateAsync("Positioning");
        var reviewBuilder = new ReviewSnapshotBuilder(
            scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog,
            scope.Evidence, new GameEventsRepository(scope.ConnectionFactory),
            scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod,
            scope.Config, scope.ReviewDrafts, NullLogger<ReviewSnapshotBuilder>.Instance);
        var objectiveBuilder = new ObjectiveGamesSnapshotBuilder(
            scope.Objectives, scope.Evidence, scope.Vod, NullLogger<ObjectiveGamesSnapshotBuilder>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await ClipPersistence.PersistAsync(scope.Vod, scope.Objectives, scope.Evidence,
                game.GameId, 100 + i * 100, 130 + i * 100,
                $"synthetic-clip-{i}.mp4", $"Tempo decision {i}", "", tempoId);
        }
        var savedEvidence = await scope.Evidence.GetForObjectiveAsync(tempoId);
        foreach (var item in savedEvidence)
        {
            // The review's Good and attach actions are independent immediate
            // writes; saving the main review form is not needed for these.
            await scope.Evidence.UpdatePolarityAsync(item.Id, "good");
            await scope.Evidence.UpdateStatusAsync(item.Id, EvidenceStatuses.Evidence);
            await scope.Evidence.UpdateObjectiveAsync(item.Id, tempoId);
        }

        var review = await reviewBuilder.BuildAsync(game.GameId);
        var reviewedTempo = Assert.Single(review.Subject!.Objectives, o => o.Id == tempoId);
        Assert.Equal(3, reviewedTempo.UnpromptedClips.Count);
        Assert.All(reviewedTempo.UnpromptedClips, clip =>
        {
            Assert.Contains(savedEvidence, e => e.Id == clip.EvidenceId);
            Assert.Equal(tempoId, clip.ObjectiveId);
            Assert.Equal("Tempo", clip.ObjectiveTitle);
            Assert.Equal("good", clip.Polarity);
            Assert.Equal("#8ee7ba", clip.PolarityColorHex);
        });

        var wire = JsonSerializer.SerializeToElement(reviewedTempo.UnpromptedClips[0],
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(tempoId, wire.GetProperty("objectiveId").GetInt64());
        Assert.Equal("Tempo", wire.GetProperty("objectiveTitle").GetString());

        var objective = await objectiveBuilder.BuildAsync(tempoId);
        Assert.Equal(3, objective.Evidence.Count);
        Assert.All(objective.Evidence, item => Assert.Equal("good", item.Polarity));
        Assert.Contains("3 good  /  0 bad  /  0 neutral", objective.EvidenceSummary);

        // A later change must survive a fresh snapshot on both pages and move
        // only the selected card to its newly attached objective.
        var changed = savedEvidence[0];
        await scope.Evidence.UpdateObjectiveAsync(changed.Id, positioningId);
        await scope.Evidence.UpdatePolarityAsync(changed.Id, "bad");

        review = await reviewBuilder.BuildAsync(game.GameId);
        reviewedTempo = Assert.Single(review.Subject!.Objectives, o => o.Id == tempoId);
        Assert.Equal(2, reviewedTempo.UnpromptedClips.Count);
        var positioning = Assert.Single(review.Subject.Objectives, o => o.Id == positioningId);
        var moved = Assert.Single(positioning.UnpromptedClips);
        Assert.Equal(changed.Id, moved.EvidenceId);
        Assert.Equal(positioningId, moved.ObjectiveId);
        Assert.Equal("Positioning", moved.ObjectiveTitle);
        Assert.Equal("bad", moved.Polarity);
        Assert.Equal("#f3a3a8", moved.PolarityColorHex);
        Assert.Contains("2 good  /  0 bad  /  0 neutral",
            (await objectiveBuilder.BuildAsync(tempoId)).EvidenceSummary);
        var movedObjectiveCard = Assert.Single((await objectiveBuilder.BuildAsync(positioningId)).Evidence);
        Assert.Equal(changed.Title, movedObjectiveCard.Title);
        Assert.Equal("bad", movedObjectiveCard.Polarity);

        await scope.Evidence.UpdateObjectiveAsync(changed.Id, null);
        review = await reviewBuilder.BuildAsync(game.GameId);
        var detached = Assert.Single(review.Subject!.UnsortedClips);
        Assert.Equal(changed.Id, detached.EvidenceId);
        Assert.Null(detached.ObjectiveId);
        Assert.Equal("", detached.ObjectiveTitle);
        Assert.Equal("bad", detached.Polarity);
        Assert.Empty((await objectiveBuilder.BuildAsync(positioningId)).Evidence);
    }
}
