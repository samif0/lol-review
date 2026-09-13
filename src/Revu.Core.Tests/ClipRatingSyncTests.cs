using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

public sealed class ClipRatingSyncTests
{
    [Fact]
    public async Task BookmarkQuality_UpdatesObjectiveEvidence_WithoutChangingOtherReviewData()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var clip = await SeedClipAsync(scope);
        var originalBookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
        var originalEvidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
        var originalScore = (await scope.Objectives.GetAsync(clip.ObjectiveId))!.Score;

        foreach (var quality in new[] { "good", "bad", "neutral", "good", "" })
        {
            await scope.Vod.UpdateBookmarkAsync(clip.BookmarkId, quality: quality);

            var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
            var evidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
            Assert.Equal(originalBookmark with { Quality = quality }, bookmark);
            Assert.Equal(originalEvidence with
            {
                Polarity = EvidencePolarities.Normalize(quality),
                UpdatedAt = evidence.UpdatedAt,
            }, evidence);
            Assert.Equal(originalScore, (await scope.Objectives.GetAsync(clip.ObjectiveId))!.Score);
        }
    }

    [Fact]
    public async Task EvidencePolarity_UpdatesBookmarkQuality_WithoutChangingOtherReviewData()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var clip = await SeedClipAsync(scope);
        var originalBookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
        var originalEvidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
        var originalScore = (await scope.Objectives.GetAsync(clip.ObjectiveId))!.Score;

        foreach (var input in new[] { " GOOD ", "bad", "neutral", "good", "" })
        {
            var expected = EvidencePolarities.Normalize(input);
            await scope.Evidence.UpdatePolarityAsync(clip.EvidenceId, input);

            var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
            var evidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
            Assert.Equal(originalBookmark with { Quality = expected }, bookmark);
            Assert.Equal(originalEvidence with { Polarity = expected, UpdatedAt = evidence.UpdatedAt }, evidence);
            Assert.Equal(originalScore, (await scope.Objectives.GetAsync(clip.ObjectiveId))!.Score);
        }
    }

    [Fact]
    public async Task RatingSync_RequiresMatchingClipKindBookmarkIdAndGameId()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var clip = await SeedClipAsync(scope);
        const long otherGameId = 99001;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(otherGameId, champion: "Jinx", win: false));
        var otherBookmarkId = await scope.Vod.AddBookmarkAsync(clip.GameId, 400, "Other clip",
            clipStartSeconds: 390, clipEndSeconds: 420, clipPath: @"C:\synthetic\other.mp4");
        var nonClipId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            clip.GameId, EvidenceKinds.TimelineRegion, clip.BookmarkId, "same-id-nonclip", 100, 130, "Timeline event"));
        var crossGameId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            otherGameId, EvidenceKinds.Clip, clip.BookmarkId, "same-id-other-game", 100, 130, "Another game"));
        var otherEvidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            clip.GameId, EvidenceKinds.Clip, otherBookmarkId, "different-clip", 390, 420, "Other clip"));

        await scope.Vod.UpdateBookmarkAsync(clip.BookmarkId, quality: "good");
        var rows = await scope.Evidence.GetForGameAsync(clip.GameId);
        Assert.Equal("good", rows.Single(e => e.Id == clip.EvidenceId).Polarity);
        Assert.Equal("neutral", rows.Single(e => e.Id == nonClipId).Polarity);
        Assert.Equal("neutral", rows.Single(e => e.Id == otherEvidenceId).Polarity);
        Assert.Equal("neutral", Assert.Single(await scope.Evidence.GetForGameAsync(otherGameId)).Polarity);

        await scope.Evidence.UpdatePolarityAsync(nonClipId, "bad");
        await scope.Evidence.UpdatePolarityAsync(crossGameId, "bad");
        var bookmarks = await scope.Vod.GetBookmarksAsync(clip.GameId);
        Assert.Equal("good", bookmarks.Single(b => b.Id == clip.BookmarkId).Quality);
        Assert.Equal("", bookmarks.Single(b => b.Id == otherBookmarkId).Quality);

        await scope.Evidence.UpdatePolarityAsync(clip.EvidenceId, "neutral");
        bookmarks = await scope.Vod.GetBookmarksAsync(clip.GameId);
        Assert.Equal("neutral", bookmarks.Single(b => b.Id == clip.BookmarkId).Quality);
        Assert.Equal("", bookmarks.Single(b => b.Id == otherBookmarkId).Quality);
    }

    [Fact]
    public async Task NoteOnlyBookmarkEdit_DoesNotResetSavedEvidenceRating()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var clip = await SeedClipAsync(scope);
        await scope.Evidence.UpdatePolarityAsync(clip.EvidenceId, "good");

        await scope.Vod.UpdateBookmarkAsync(clip.BookmarkId, note: "Updated clip note");

        var evidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
        var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
        Assert.Equal("good", evidence.Polarity);
        Assert.Equal("good", bookmark.Quality);
        Assert.Equal("Updated clip note", bookmark.Note);
    }

    [Fact]
    public async Task BookmarkRatingFailure_RollsBackBothRecords()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var clip = await SeedClipAsync(scope);
        var beforeBookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
        var beforeEvidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
        using (var connection = scope.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_evidence_rating BEFORE UPDATE OF polarity ON evidence_items
                BEGIN SELECT RAISE(ABORT, 'Synthetic rating failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => scope.Vod.UpdateBookmarkAsync(clip.BookmarkId, quality: "good"));

        Assert.Equal(beforeBookmark, Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId)));
        Assert.Equal(beforeEvidence, Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId)));
    }

    [Fact]
    public async Task EvidenceRatingFailure_RollsBackBothRecords()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var clip = await SeedClipAsync(scope);
        var beforeBookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId));
        var beforeEvidence = Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId));
        using (var connection = scope.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_bookmark_rating BEFORE UPDATE OF quality ON vod_bookmarks
                BEGIN SELECT RAISE(ABORT, 'Synthetic rating failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => scope.Evidence.UpdatePolarityAsync(clip.EvidenceId, "good"));

        Assert.Equal(beforeBookmark, Assert.Single(await scope.Vod.GetBookmarksAsync(clip.GameId)));
        Assert.Equal(beforeEvidence, Assert.Single(await scope.Evidence.GetForObjectiveAsync(clip.ObjectiveId)));
    }

    private static async Task<ClipFixture> SeedClipAsync(TestDatabaseScope scope)
    {
        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Watch support positioning", "laning");
        var bookmarkId = await scope.Vod.AddBookmarkAsync(gameId, 100, "Bookmark note",
            clipStartSeconds: 100, clipEndSeconds: 130, clipPath: @"C:\synthetic\trade.mp4",
            objectiveId: objectiveId, quality: "neutral");
        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            gameId, EvidenceKinds.Clip, bookmarkId, $"clip:{bookmarkId}", 100, 130, "Clip title",
            Note: "Evidence note", ObjectiveId: objectiveId, Status: EvidenceStatuses.Highlight));
        return new ClipFixture(gameId, objectiveId, bookmarkId, evidenceId);
    }

    private sealed record ClipFixture(long GameId, long ObjectiveId, long BookmarkId, long EvidenceId);
}
