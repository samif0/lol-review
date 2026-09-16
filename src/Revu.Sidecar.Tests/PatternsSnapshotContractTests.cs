using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The cross-pattern snapshot revisits saved clips/bookmarks and highlights
/// repeated mistakes on current objectives. Automatic events are never a
/// review playlist; bookmark playback is a read-only, padded VOD preview.
/// </summary>
public sealed class PatternsSnapshotContractTests
{
    private static PatternsSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Evidence,
        NullLogger<PatternsSnapshotBuilder>.Instance);

    private sealed class TempVods : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"revu-test-vods-{Guid.NewGuid():N}");
        public TempVods() { Directory.CreateDirectory(_dir); }
        public string For(long gameId)
        {
            var path = Path.Combine(_dir, $"{gameId}.mp4");
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        }
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }
    }

    private static async Task<(long ObjectiveId, long[] Games)> SeedSavedMistakesAsync(
        SidecarWriteScope scope, long firstGameId = 6601, string title = "Use the time between waves")
    {
        var objectiveId = await scope.Objectives.CreateAsync(title, "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new long[2];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = (await scope.SeedGameAsync(gameId: firstGameId + i, timestamp: now - (i + 1) * 3600)).GameId;
            await scope.Vod.AddBookmarkAsync(games[i], 300 + i * 100,
                note: "Stayed in lane after the wave was pushed", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        }
        return (objectiveId, games);
    }

    [Fact]
    public async Task BuildAsync_AutomaticKillsDeathsAndFailedCriteria_DoNotCreateReviewPlaylists()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Choose better fights", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "KILL", "DEATH" });
        var materializer = new PatternEvidenceMaterializer(new GameEventsRepository(scope.ConnectionFactory),
            scope.Evidence, scope.Objectives, scope.Games, NullLogger<PatternEvidenceMaterializer>.Instance);
        for (var i = 0; i < 3; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6701 + i);
            await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, practiced: true);
            await scope.Objectives.SetCriteriaMetAsync(game.GameId, objectiveId, met: false);
            await materializer.MaterializeReviewSignalsAsync(game.GameId);
            foreach (var token in new[] { "KILL", "DEATH" })
            {
                for (var n = 0; n < 18; n++)
                {
                    var time = 100 + n * 30;
                    await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                        GameId: game.GameId,
                        SourceKind: EvidenceKinds.TimelineRegion,
                        SourceId: null,
                        SourceKey: PatternConstants.ObjEventSourceKey(token, time),
                        StartTimeSeconds: time - PatternConstants.MomentLeadSeconds,
                        EndTimeSeconds: time + PatternConstants.MomentTrailSeconds,
                        Title: PatternConstants.TokenLabel(token),
                        ObjectiveId: objectiveId,
                        Polarity: EvidencePolarities.Bad,
                        Status: EvidenceStatuses.Evidence));
                }
            }
        }

        var snapshot = await Builder(scope).BuildAsync();

        Assert.Empty(snapshot.Patterns);
        Assert.False(snapshot.HasPending);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal("", snapshot.ErrorText);
    }

    [Theory]
    [InlineData(0, 0, 15, "0:00")]
    [InlineData(5, 0, 20, "0:05")]
    [InlineData(300, 285, 315, "5:00")]
    [InlineData(1795, 1780, 1800, "29:55")]
    public async Task BuildAsync_BookmarksUseFifteenSecondPreviewPadding_WithoutSavingAClip(
        int bookmarkTime, int previewStart, int previewEnd, string timeLabel)
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var game = await scope.SeedGameAsync(durationSeconds: 1800);
        var vodPath = vods.For(game.GameId);
        await scope.Vod.LinkVodAsync(game.GameId, vodPath);
        await File.WriteAllTextAsync(vodPath + RecordingTimeline.Suffix,
            System.Text.Json.JsonSerializer.Serialize(new RecordingTiming(1, game.GameId, Path.GetFileName(vodPath), -45.125),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var bookmarkId = await scope.Vod.AddBookmarkAsync(game.GameId, bookmarkTime, "Rotate after this wave", objectiveId: objectiveId);
        var before = Assert.Single(await scope.Vod.GetBookmarksAsync(game.GameId));
        var evidenceBefore = await scope.Evidence.GetForGameAsync(game.GameId);

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns);
        Assert.Equal(PatternConstants.KindSavedObjectiveEvidence, card.Kind);
        Assert.Equal("saved", card.ReviewMode);
        var moment = Assert.Single(card.Moments);
        Assert.Equal(bookmarkId, moment.BookmarkId);
        Assert.Equal(previewStart, moment.StartTimeSeconds);
        Assert.Equal(previewEnd, moment.EndTimeSeconds);
        Assert.Equal(timeLabel, moment.TimeLabel);
        Assert.EndsWith(timeLabel, moment.VideoHeaderText);
        Assert.True(moment.HasVod);
        Assert.False(moment.HasClip);
        Assert.Equal("", moment.ClipPath);
        Assert.Equal(-45.125, moment.GameTimeAtVideoStart);
        Assert.Equal(before, Assert.Single(await scope.Vod.GetBookmarksAsync(game.GameId)));
        Assert.Equal(evidenceBefore, await scope.Evidence.GetForGameAsync(game.GameId));
        var coreCard = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        var coreMoment = Assert.Single(await scope.Evidence.GetPatternMomentsAsync(coreCard));
        Assert.Equal(bookmarkTime, coreMoment.StartTimeSeconds);
        Assert.Equal(bookmarkTime, coreMoment.EndTimeSeconds);
    }

    [Fact]
    public async Task BuildAsync_KeptClipKeepsItsSavedRange_AndPlaysWithoutTheOriginalRecording()
    {
        using var scope = new SidecarWriteScope();
        using var clips = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var game = await scope.SeedGameAsync();
        var clipPath = clips.For(game.GameId);
        var bookmarkId = await scope.Vod.AddBookmarkAsync(game.GameId, 300, "Good rotation",
            clipStartSeconds: 292, clipEndSeconds: 323, clipPath: clipPath,
            objectiveId: objectiveId, quality: EvidencePolarities.Good);

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns);
        var moment = Assert.Single(card.Moments);
        Assert.Equal(bookmarkId, moment.BookmarkId);
        Assert.Equal(292, moment.StartTimeSeconds);
        Assert.Equal(323, moment.EndTimeSeconds);
        Assert.Equal(clipPath, moment.ClipPath);
        Assert.True(moment.HasClip);
        Assert.False(moment.HasVod);
        Assert.Equal(EvidenceKinds.Clip, moment.SourceKind);
        Assert.Equal(0, card.UnwatchableMomentCount);
        Assert.False(snapshot.HasPending);
    }

    [Fact]
    public async Task BuildAsync_MissingRecordingsKeepSavedNotesInThePlaylist()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 3; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6801 + i, timestamp: now - i * 3600);
            await scope.Vod.AddBookmarkAsync(game.GameId, 300, $"My saved lesson {i}", objectiveId: objectiveId);
            if (i == 0) await scope.Vod.LinkVodAsync(game.GameId, vods.For(game.GameId));
            if (i == 1) await scope.Vod.LinkVodAsync(game.GameId, Path.Combine(Path.GetTempPath(), $"deleted-{Guid.NewGuid():N}.mp4"));
        }

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns);
        Assert.Equal(3, card.TotalMomentCount);
        Assert.Equal(3, card.MomentCount);
        Assert.Equal(3, card.GameCount);
        Assert.Equal(2, card.UnwatchableMomentCount);
        Assert.Equal(1, card.Moments.Count(moment => moment.HasVod));
        Assert.All(card.Moments, moment => Assert.True(moment.HasNote));
        Assert.All(card.Moments.Where(moment => !moment.HasVod), moment =>
        {
            Assert.Equal("", moment.VodPath);
            Assert.False(moment.HasClip);
            Assert.StartsWith("My saved lesson", moment.Note);
        });
    }

    [Fact]
    public async Task BuildAsync_AllSavedMomentsRemainAvailableBeyondTheOldPlaylistCap()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var game = await scope.SeedGameAsync();
        const int count = 31;
        var bookmarks = new List<long>();
        for (var i = 0; i < count; i++)
            bookmarks.Add(await scope.Vod.AddBookmarkAsync(game.GameId, 100 + i * 30,
                $"Saved lesson {i}", objectiveId: objectiveId));

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns);
        Assert.Equal(count, card.TotalMomentCount);
        Assert.Equal(count, card.MomentCount);
        Assert.Equal(bookmarks, card.Moments.Select(moment => moment.BookmarkId!.Value));
        Assert.Equal(Enumerable.Range(1, count), card.Moments.Select(moment => moment.Ordinal));
        Assert.Equal(card.Moments.OrderBy(moment => moment.GameTimestamp).ThenBy(moment => moment.StartTimeSeconds)
            .Select(moment => moment.BookmarkId), card.Moments.Select(moment => moment.BookmarkId));
        Assert.False(snapshot.HasPending);
        Assert.Equal(0, snapshot.PendingCount);
    }

    [Fact]
    public async Task BuildAsync_SavedCollectionsIncludeOlderReviewHistory_WithoutCreatingAnOldMistakeTrend()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 2; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6901 + i,
                timestamp: now - (PatternConstants.WindowDays + 2 + i) * 86400L);
            await scope.Vod.AddBookmarkAsync(game.GameId, 300, "Revisit this lesson",
                objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        }

        var snapshot = await Builder(scope).BuildAsync();

        var saved = Assert.Single(snapshot.Patterns);
        Assert.Equal(PatternConstants.KindSavedObjectiveEvidence, saved.Kind);
        Assert.Equal("saved", saved.ReviewMode);
        Assert.Equal(2, saved.MomentCount);
        Assert.False(snapshot.HasPending);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal("", snapshot.EmptyText);
    }

    [Fact]
    public async Task BuildAsync_RepeatedMistakesRequireTwoRecentGames_AndExcludeGoodOrNeutralMoments()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var first = await scope.SeedGameAsync(gameId: 7001);
        for (var i = 0; i < 18; i++)
            await scope.Vod.AddBookmarkAsync(first.GameId, 100 + i * 30, objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        var oneGame = await Builder(scope).BuildAsync();
        Assert.Single(oneGame.Patterns);
        Assert.False(oneGame.HasPending);

        var second = await scope.SeedGameAsync(gameId: 7002);
        await scope.Vod.AddBookmarkAsync(second.GameId, 300, objectiveId: objectiveId, quality: EvidencePolarities.Good);
        await scope.Vod.AddBookmarkAsync(second.GameId, 600, objectiveId: objectiveId);
        Assert.DoesNotContain((await Builder(scope).BuildAsync()).Patterns, card => card.ReviewMode == "trend");
        var badBookmark = await scope.Vod.AddBookmarkAsync(second.GameId, 900,
            objectiveId: objectiveId, quality: EvidencePolarities.Bad);

        var snapshot = await Builder(scope).BuildAsync();

        var saved = Assert.Single(snapshot.Patterns, card => card.ReviewMode == "saved");
        var trend = Assert.Single(snapshot.Patterns, card => card.ReviewMode == "trend");
        Assert.Equal(PatternConstants.KindBadObjectiveEvidence, trend.Kind);
        Assert.Equal(21, saved.MomentCount);
        Assert.Equal(19, trend.MomentCount);
        Assert.Equal(2, trend.GameCount);
        Assert.All(trend.Moments, moment => Assert.Equal(EvidencePolarities.Bad, moment.Polarity));
        Assert.Contains(trend.Moments, moment => moment.BookmarkId == badBookmark);
        Assert.True(snapshot.HasPending);
        Assert.Equal(1, snapshot.PendingCount);
    }

    [Fact]
    public async Task BuildAsync_CompletedObjectivesAndUntaggedSavesDoNotEnterCurrentObjectiveCollections()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var currentId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var completedId = await scope.Objectives.CreateAsync("Old focus", "macro");
        await scope.Objectives.MarkCompleteAsync(completedId);
        var game = await scope.SeedGameAsync();
        var currentBookmark = await scope.Vod.AddBookmarkAsync(game.GameId, 100, objectiveId: currentId);
        await scope.Vod.AddBookmarkAsync(game.GameId, 200, objectiveId: completedId);
        await scope.Vod.AddBookmarkAsync(game.GameId, 300);

        var snapshot = await Builder(scope).BuildAsync();

        var saved = Assert.Single(snapshot.Patterns);
        Assert.Equal(currentId, saved.ObjectiveId);
        Assert.Equal(currentBookmark, Assert.Single(saved.Moments).BookmarkId);
    }

    [Fact]
    public async Task BuildAsync_ReviewedTrendsRemainRevisitable_AndRearmOnlyAfterTwoNewSavedMistakes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var (objectiveId, _) = await SeedSavedMistakesAsync(scope);
        var patternKey = $"{PatternConstants.KindBadObjectiveEvidence}:obj{objectiveId}";
        await scope.Evidence.MarkPatternReviewedAsync(patternKey, PatternConstants.KindBadObjectiveEvidence, 2);
        var reviewedAt = (await scope.Evidence.GetReviewedPatternsAsync())[patternKey];

        var closed = await Builder(scope).BuildAsync();
        var reviewed = Assert.Single(closed.Patterns, card => card.ReviewMode == "trend");
        Assert.True(reviewed.IsReviewed);
        Assert.Equal(2, reviewed.MomentCount);
        Assert.Single(closed.Patterns, card => card.ReviewMode == "saved");
        Assert.False(closed.HasPending);
        Assert.Equal(0, closed.PendingCount);
        Assert.Equal(1, closed.ReviewedPatternCount);

        for (var i = 0; i < PatternConstants.ReArmNewMoments; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6603 + i);
            var bookmarkId = await scope.Vod.AddBookmarkAsync(game.GameId, 400, "A new saved mistake",
                objectiveId: objectiveId, quality: EvidencePolarities.Bad);
            using var conn = scope.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE vod_bookmarks SET created_at = @createdAt WHERE id = @bookmarkId";
            cmd.Parameters.AddWithValue("@createdAt", reviewedAt + 1);
            cmd.Parameters.AddWithValue("@bookmarkId", bookmarkId);
            await cmd.ExecuteNonQueryAsync();

            var snapshot = await Builder(scope).BuildAsync();
            var trend = Assert.Single(snapshot.Patterns, card => card.ReviewMode == "trend");
            Assert.Equal(i + 1 < PatternConstants.ReArmNewMoments, trend.IsReviewed);
            Assert.Equal(i + 1 >= PatternConstants.ReArmNewMoments, snapshot.HasPending);
            Assert.Equal(i + 1 >= PatternConstants.ReArmNewMoments ? 1 : 0, snapshot.PendingCount);
        }

        var rearmed = await Builder(scope).BuildAsync();
        Assert.Equal(PatternConstants.ReArmNewMoments,
            Assert.Single(rearmed.Patterns, card => card.ReviewMode == "trend").NewMomentCount);
        Assert.Equal(reviewedAt, (await scope.Evidence.GetReviewedPatternsAsync())[patternKey]);
    }

    [Fact]
    public async Task BuildAsync_ReviewedTrendsCannotCrowdSavedCollectionsOutOfTheSnapshot()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectives = new List<long>();
        for (var i = 0; i < 8; i++)
        {
            var (objectiveId, _) = await SeedSavedMistakesAsync(scope, firstGameId: 7101 + i * 2, title: $"Current focus {i}");
            objectives.Add(objectiveId);
            await scope.Evidence.MarkPatternReviewedAsync($"{PatternConstants.KindBadObjectiveEvidence}:obj{objectiveId}",
                PatternConstants.KindBadObjectiveEvidence, 2);
        }

        var snapshot = await Builder(scope).BuildAsync();

        Assert.Equal(objectives.OrderBy(id => id),
            snapshot.Patterns.Where(card => card.ReviewMode == "saved").Select(card => card.ObjectiveId!.Value).OrderBy(id => id));
        Assert.False(snapshot.HasPending);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(8, snapshot.ReviewedPatternCount);
        Assert.All(snapshot.Patterns.Where(card => card.ReviewMode == "trend"), card => Assert.True(card.IsReviewed));
    }

    [Fact]
    public async Task BuildAsync_EmptyDatabaseExplainsSavedMomentsAndCurrentObjectives()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var snapshot = await Builder(scope).BuildAsync();

        Assert.Empty(snapshot.Patterns);
        Assert.Equal("", snapshot.ErrorText);
        Assert.Contains("clip", snapshot.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bookmark", snapshot.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("objective", snapshot.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("structured criteria", snapshot.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PatternConstants.WindowDays, snapshot.WindowDays);
    }

    [Fact]
    public async Task BuildAsync_RepositoryFailureSurfacesErrorText_NotACleanEmptyState()
    {
        var builder = new PatternsSnapshotBuilder(new ThrowingEvidenceRepository(),
            NullLogger<PatternsSnapshotBuilder>.Instance);

        var snapshot = await builder.BuildAsync();

        Assert.Empty(snapshot.Patterns);
        Assert.NotEqual("", snapshot.ErrorText);
        Assert.Equal("", snapshot.EmptyText);
    }

    private sealed class ThrowingEvidenceRepository : IEvidenceRepository
    {
        private static Exception Boom() => new InvalidOperationException("database is locked");
        public Task<long> UpsertAsync(EvidenceUpsert item) => throw Boom();
        public Task<IReadOnlyList<EvidenceItemRecord>> GetForGameAsync(long gameId, bool includeDismissed = false) => throw Boom();
        public Task<IReadOnlyList<EvidenceItemRecord>> GetForObjectiveAsync(long objectiveId, bool includeDismissed = false) => throw Boom();
        public Task<IReadOnlyList<EvidenceItemRecord>> GetRecentAsync(int limit = 20, bool includeDismissed = false) => throw Boom();
        public Task<int> CountPendingAsync() => throw Boom();
        public Task UpdateStatusAsync(long evidenceId, string status) => throw Boom();
        public Task DeleteAsync(long evidenceId) => throw Boom();
        public Task UpdatePolarityAsync(long evidenceId, string polarity) => throw Boom();
        public Task UpdateObjectiveAsync(long evidenceId, long? objectiveId) => throw Boom();
        public Task UpdatePromptAsync(long evidenceId, long? promptId) => throw Boom();
        public Task UpdateNoteAsync(long evidenceId, string note) => throw Boom();
        public Task AttachClipToEvidenceAsync(long evidenceId, long bookmarkId, int clipStartS, int clipEndS) => throw Boom();
        public Task<IReadOnlyList<ObjectivePatternCard>> GetPatternCardsAsync(int limit = 6) => throw Boom();
        public Task<IReadOnlyList<PatternMoment>> GetPatternMomentsAsync(ObjectivePatternCard pattern) => throw Boom();
        public Task MarkPatternReviewedAsync(string patternKey, string kind, int momentCount) => throw Boom();
        public Task<int> CountReviewedPatternsAsync() => throw Boom();
        public Task<IReadOnlyDictionary<string, long>> GetReviewedPatternsAsync() => throw Boom();
        public Task<long?> FindPromotedTwinAsync(long gameId, string title, int startTimeSeconds, int endTimeSeconds) => throw Boom();
        public Task<int> DeleteBySourceKeyAsync(long gameId, string sourceKind, string sourceKey) => throw Boom();
    }
}
