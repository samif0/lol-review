using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class ObjectiveEvidenceRatingTests
{
    [Fact]
    public async Task ClipRatings_ShowTheSameJudgementInObjectiveCardsAndSummary()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(gameId: 8701);
        var objectiveId = await scope.Objectives.CreateAsync("Track cooldowns");
        var builder = new ObjectiveGamesSnapshotBuilder(
            scope.Objectives, scope.Evidence, scope.Vod,
            NullLogger<ObjectiveGamesSnapshotBuilder>.Instance);

        // Cover all three rating entry points: during clip creation, editing an
        // existing clip's quality, and rating its evidence card in the review.
        var bookmarkIds = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            bookmarkIds.Add(await ClipPersistence.PersistAsync(
                scope.Vod, scope.Objectives, scope.Evidence, game.GameId,
                startS: 100 + 100 * i, endS: 130 + 100 * i,
                clipPath: $"clip-{i}.mp4", note: $"Wait for cooldown {i}",
                quality: i == 0 ? "good" : "", objectiveId: objectiveId));
        }
        await scope.Vod.UpdateBookmarkAsync(bookmarkIds[1], quality: "good");
        var items = await scope.Evidence.GetForObjectiveAsync(objectiveId);
        var lastEvidence = Assert.Single(items, e => e.SourceId == bookmarkIds[2]);
        await scope.Evidence.UpdatePolarityAsync(lastEvidence.Id, "good");

        var page = await builder.BuildAsync(objectiveId);
        Assert.Equal(3, page.Evidence.Count);
        Assert.All(page.Evidence, row =>
        {
            Assert.Equal("good", row.Polarity);
            Assert.Equal("Good example", row.PolarityLabel);
        });
        Assert.Contains("3 good  /  0 bad  /  0 neutral", page.EvidenceSummary);
        Assert.All(await scope.Vod.GetBookmarksAsync(game.GameId), b => Assert.Equal("good", b.Quality));

        // A later deliberate Neutral choice must replace Good in both views.
        await scope.Evidence.UpdatePolarityAsync(lastEvidence.Id, "neutral");
        var rerated = await builder.BuildAsync(objectiveId);
        Assert.Contains("2 good  /  0 bad  /  1 neutral", rerated.EvidenceSummary);
        var neutral = Assert.Single(rerated.Evidence, e => e.Polarity == "neutral");
        Assert.Equal("Neutral", neutral.PolarityLabel);
        Assert.Equal("Wait for cooldown 2", neutral.Title);
        Assert.Equal("neutral", (await scope.Vod.GetBookmarksAsync(game.GameId))
            .Single(b => b.Id == bookmarkIds[2]).Quality);
    }
}
