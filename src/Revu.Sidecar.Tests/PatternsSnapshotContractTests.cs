using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract tests for the objective-driven GET /api/patterns snapshot:
///   • a backend failure surfaces as errorText (never a clean "no patterns yet")
///   • start-less game-level moments render without a fabricated 0:00
///   • only WATCHABLE moments enter a playlist (recording on disk, or a kept clip)
///   • a playlist is capped, keeping noted/clipped moments then the newest
///   • reviewed patterns re-arm through the shared PatternReviewGate watermark
///   • pending cards rank ahead of reviewed ones under the display cap
/// </summary>
public sealed class PatternsSnapshotContractTests
{
    private static PatternEvidenceMaterializer Materializer(SidecarWriteScope scope) => new(
        new GameEventsRepository(scope.ConnectionFactory),
        scope.Evidence,
        scope.Objectives,
        scope.Games,
        NullLogger<PatternEvidenceMaterializer>.Instance);

    private static PatternsSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Evidence,
        NullLogger<PatternsSnapshotBuilder>.Instance);

    /// <summary>Real (empty) recording files so File.Exists says yes — the
    /// playlist only admits moments that can actually be watched. One file per
    /// game: vod_files refuses to link one file to two games.</summary>
    private sealed class TempVods : IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"revu-test-vods-{Guid.NewGuid():N}");
        public TempVods() { Directory.CreateDirectory(_dir); }
        public string For(long gameId)
        {
            var path = System.IO.Path.Combine(_dir, $"{gameId}.mp4");
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        }
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }
    }

    /// <summary>Seed the objective_criteria pattern at full failure: three
    /// recent games, each failing the objective's structured criterion, with
    /// their materialized anchors (fail share 100% → high severity).</summary>
    private static async Task<long> SeedFailingCriterionAsync(SidecarWriteScope scope, TempVods vods, string title = "CS 7+/min by 10")
    {
        var materializer = Materializer(scope);
        var objectiveId = await scope.Objectives.CreateAsync(title, "laning");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 3; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6601 + i, timestamp: now - (i + 1) * 3600);
            await scope.Vod.LinkVodAsync(game.GameId, vods.For(game.GameId));
            await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, practiced: true);
            await scope.Objectives.SetCriteriaMetAsync(game.GameId, objectiveId, met: false);
            await materializer.MaterializeReviewSignalsAsync(game.GameId);
        }
        return objectiveId;
    }

    /// <summary>Seed a pending objective_events pattern: an objective tracking
    /// DEATH plus five materialized death anchors across three games
    /// (count 5 → medium severity).</summary>
    private static async Task<long[]> SeedTrackedDeathsAsync(SidecarWriteScope scope, TempVods? vods, long firstGameId = 6701)
    {
        var objectiveId = await scope.Objectives.CreateAsync("Track deaths", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "DEATH" });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new long[3];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = (await scope.SeedGameAsync(gameId: firstGameId + i, timestamp: now - (i + 1) * 3600)).GameId;
            if (vods is not null) await scope.Vod.LinkVodAsync(games[i], vods.For(games[i]));
        }
        foreach (var (g, t) in new[] { (games[0], 300), (games[0], 700), (games[1], 400), (games[1], 800), (games[2], 500) })
        {
            await scope.Evidence.UpsertAsync(DeathAnchor(g, t));
        }
        return games;
    }

    private static EvidenceUpsert DeathAnchor(long gameId, int t) => new(
        GameId: gameId,
        SourceKind: EvidenceKinds.TimelineRegion,
        SourceId: null,
        SourceKey: PatternConstants.ObjEventSourceKey("DEATH", t),
        StartTimeSeconds: t - PatternConstants.MomentLeadSeconds,
        EndTimeSeconds: t + PatternConstants.MomentTrailSeconds,
        Title: PatternConstants.TokenLabel("DEATH"),
        Polarity: EvidencePolarities.Bad,
        Status: EvidenceStatuses.Evidence);

    [Fact]
    public async Task BuildAsync_StartLessMoments_RenderWithoutFabricatedTime()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        await SeedFailingCriterionAsync(scope, vods);

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindObjectiveCriteria);
        Assert.Equal(3, card.MomentCount);
        Assert.Equal(3, card.TotalMomentCount);
        Assert.Equal(0, card.UnwatchableMomentCount);
        Assert.Equal("high", card.Severity); // 100% fail share
        Assert.All(card.Moments, m =>
        {
            Assert.Null(m.StartTimeSeconds);
            Assert.Equal("", m.TimeLabel);
            Assert.DoesNotContain("0:00", m.VideoHeaderText);
        });
        Assert.Equal("", snapshot.ErrorText);
        Assert.Equal(PatternConstants.WindowDays, snapshot.WindowDays);
    }

    [Fact]
    public async Task BuildAsync_MomentsWithNothingLeftToWatch_StayOutOfThePlaylist_ButStillCount()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var games = await SeedTrackedDeathsAsync(scope, vods: null);

        // games[0]: recording on disk. games[2]: vod_files row outlived its file
        // (Ascent retention). games[1]: never recorded. Only games[0]'s two
        // moments can be watched.
        await scope.Vod.LinkVodAsync(games[2], Path.Combine(Path.GetTempPath(), "definitely-deleted.mp4"));
        var realVod = vods.For(games[0]);
        await scope.Vod.LinkVodAsync(games[0], realVod);

        var snapshot = await Builder(scope).BuildAsync();
        var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindObjectiveEvents);

        Assert.Equal(5, card.TotalMomentCount);
        Assert.Equal(2, card.MomentCount);
        Assert.Equal(3, card.UnwatchableMomentCount);
        Assert.Equal("2 of 5 moments across 1 game", card.Subtitle);
        Assert.All(card.Moments, m =>
        {
            Assert.Equal(games[0], m.GameId);
            Assert.True(m.HasVod);
            Assert.Equal(realVod, m.VodPath);
        });
        Assert.Equal(new[] { 1, 2 }, card.Moments.Select(m => m.Ordinal).ToArray());
    }

    [Fact]
    public async Task BuildAsync_KeptClip_MakesAMomentWatchable_EvenWhenTheRecordingIsGone()
    {
        using var scope = new SidecarWriteScope();
        using var clips = new TempVods();
        await scope.InitializeAsync();
        var games = await SeedTrackedDeathsAsync(scope, vods: null);
        var clipPath = clips.For(games[1]);

        // No recording anywhere, but the user clipped one moment of games[1] —
        // the clip file is what gets played.
        var card0 = Assert.Single(await scope.Evidence.GetPatternCardsAsync(), c => c.Kind == PatternConstants.KindObjectiveEvents);
        var moment = (await scope.Evidence.GetPatternMomentsAsync(card0)).Single(m => m.GameId == games[1] && m.StartTimeSeconds == 400 - PatternConstants.MomentLeadSeconds);
        var bookmarkId = await scope.Vod.AddBookmarkAsync(games[1], 400, "noted", clipStartSeconds: 390, clipEndSeconds: 410, clipPath: clipPath);
        await scope.Evidence.AttachClipToEvidenceAsync(moment.EvidenceId, bookmarkId, 390, 410);

        var snapshot = await Builder(scope).BuildAsync();
        var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindObjectiveEvents);

        var shown = Assert.Single(card.Moments);
        Assert.True(shown.HasClip);
        Assert.False(shown.HasVod);
        Assert.Equal(clipPath, shown.ClipPath);
        Assert.Equal(5, card.TotalMomentCount);
        Assert.Equal(4, card.UnwatchableMomentCount);
    }

    [Fact]
    public async Task BuildAsync_PlaylistIsCapped_KeepingNotedMomentsThenTheNewest_InChronologicalOrder()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var games = await SeedTrackedDeathsAsync(scope, vods);

        // Flood games[0] (the NEWEST game, timestamp now-1h) and games[2] (the
        // OLDEST) with anchors well past the cap; note one of the oldest.
        var cap = PatternConstants.PatternMomentDisplayLimit;
        for (var i = 0; i < cap; i++) await scope.Evidence.UpsertAsync(DeathAnchor(games[2], 1000 + i * 10));
        for (var i = 0; i < cap; i++) await scope.Evidence.UpsertAsync(DeathAnchor(games[0], 1000 + i * 10));
        var notedId = await scope.Evidence.UpsertAsync(DeathAnchor(games[2], 60));
        await scope.Evidence.UpdateNoteAsync(notedId, "the one I care about");

        var snapshot = await Builder(scope).BuildAsync();
        var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindObjectiveEvents);

        Assert.Equal(5 + 2 * cap + 1, card.TotalMomentCount);
        Assert.Equal(cap, card.MomentCount);
        Assert.Equal(0, card.UnwatchableMomentCount); // every game has its recording
        Assert.Equal($"{cap} of {5 + 2 * cap + 1} moments across 2 games", card.Subtitle);

        // The noted (oldest) moment survives the cap; the rest are the newest game's.
        Assert.Contains(card.Moments, m => m.EvidenceId == notedId);
        Assert.Equal(cap - 1, card.Moments.Count(m => m.GameId == games[0]));
        // Chronological (oldest-first) with 1-based ordinals numbered AFTER the cut.
        Assert.Equal(card.Moments.OrderBy(m => m.GameTimestamp).ThenBy(m => m.StartTimeSeconds).Select(m => m.EvidenceId), card.Moments.Select(m => m.EvidenceId));
        Assert.Equal(Enumerable.Range(1, cap), card.Moments.Select(m => m.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_ReviewedPattern_ReArmsThroughTheSharedGate()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();
        var objectiveId = await SeedFailingCriterionAsync(scope, vods);
        var patternKey = $"{PatternConstants.KindObjectiveCriteria}:obj{objectiveId}";

        // Mark the pattern reviewed → it closes.
        await scope.Evidence.MarkPatternReviewedAsync(patternKey, PatternConstants.KindObjectiveCriteria, 3);
        var closed = await Builder(scope).BuildAsync();
        var closedCard = Assert.Single(closed.Patterns, p => p.Kind == PatternConstants.KindObjectiveCriteria);
        Assert.True(closedCard.IsReviewed);
        Assert.False(closed.HasPending);

        // Age the review stamp behind the moments (as if the moments accrued
        // after the review): ≥2 new moments re-arm the pattern.
        using (var conn = scope.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE pattern_reviews SET reviewed_at = reviewed_at - 3600";
            await cmd.ExecuteNonQueryAsync();
        }

        var reArmed = await Builder(scope).BuildAsync();
        var reArmedCard = Assert.Single(reArmed.Patterns, p => p.Kind == PatternConstants.KindObjectiveCriteria);
        Assert.False(reArmedCard.IsReviewed);
        Assert.Equal(3, reArmedCard.NewMomentCount);
        Assert.True(reArmed.HasPending);
    }

    [Fact]
    public async Task BuildAsync_PendingCardsRankAheadOfReviewedOnes()
    {
        using var scope = new SidecarWriteScope();
        using var vods = new TempVods();
        await scope.InitializeAsync();

        // A HIGH-severity criterion card, marked reviewed, and a MEDIUM-severity
        // tracked-deaths card left pending. Severity order alone would lead
        // with the reviewed card; the pending-first cap must not.
        var objectiveId = await SeedFailingCriterionAsync(scope, vods);
        await SeedTrackedDeathsAsync(scope, vods, firstGameId: 6801);
        await scope.Evidence.MarkPatternReviewedAsync(
            $"{PatternConstants.KindObjectiveCriteria}:obj{objectiveId}",
            PatternConstants.KindObjectiveCriteria, 3);

        var snapshot = await Builder(scope).BuildAsync();

        Assert.Equal(2, snapshot.Patterns.Count);
        Assert.Equal(PatternConstants.KindObjectiveEvents, snapshot.Patterns[0].Kind);
        Assert.False(snapshot.Patterns[0].IsReviewed);
        Assert.Equal(PatternConstants.KindObjectiveCriteria, snapshot.Patterns[1].Kind);
        Assert.True(snapshot.Patterns[1].IsReviewed);
        Assert.Equal(1, snapshot.PendingCount);
    }

    [Fact]
    public async Task BuildAsync_EmptyDatabase_TellsTheTruthAboutHowPatternsBuild()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var snapshot = await Builder(scope).BuildAsync();

        Assert.Empty(snapshot.Patterns);
        Assert.Equal("", snapshot.ErrorText);
        Assert.Contains($"last {PatternConstants.WindowDays} days", snapshot.EmptyText);
        // v3.6: the copy must say patterns come from learning objectives.
        Assert.Contains("objectives", snapshot.EmptyText);
    }

    [Fact]
    public async Task BuildAsync_RepositoryFailure_SurfacesErrorText_NotACleanEmptyState()
    {
        var builder = new PatternsSnapshotBuilder(
            new ThrowingEvidenceRepository(),
            NullLogger<PatternsSnapshotBuilder>.Instance);

        var snapshot = await builder.BuildAsync();

        Assert.Empty(snapshot.Patterns);
        Assert.NotEqual("", snapshot.ErrorText);
        // The genuine-empty copy must NOT accompany a failure.
        Assert.Equal("", snapshot.EmptyText);
    }

    /// <summary>Every member throws — simulates a corrupt/locked DB under the
    /// snapshot builder.</summary>
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
