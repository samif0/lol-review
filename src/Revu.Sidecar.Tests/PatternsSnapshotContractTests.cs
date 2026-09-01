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
///   • pruned VOD files degrade to the graceful no-VOD state
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

    /// <summary>Seed the objective_criteria pattern at full failure: three
    /// recent games, each failing the objective's structured criterion, with
    /// their materialized anchors (fail share 100% → high severity).</summary>
    private static async Task<long> SeedFailingCriterionAsync(SidecarWriteScope scope, string title = "CS 7+/min by 10")
    {
        var materializer = Materializer(scope);
        var objectiveId = await scope.Objectives.CreateAsync(title, "laning");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 3; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6601 + i, timestamp: now - (i + 1) * 3600);
            await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, practiced: true);
            await scope.Objectives.SetCriteriaMetAsync(game.GameId, objectiveId, met: false);
            await materializer.MaterializeReviewSignalsAsync(game.GameId);
        }
        return objectiveId;
    }

    /// <summary>Seed a pending objective_events pattern: an objective tracking
    /// DEATH plus five materialized death anchors across three games
    /// (count 5 → medium severity).</summary>
    private static async Task<long[]> SeedTrackedDeathsAsync(SidecarWriteScope scope, long firstGameId = 6701)
    {
        var objectiveId = await scope.Objectives.CreateAsync("Track deaths", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "DEATH" });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new long[3];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = (await scope.SeedGameAsync(gameId: firstGameId + i, timestamp: now - (i + 1) * 3600)).GameId;
        }
        foreach (var (g, t) in new[] { (games[0], 300), (games[0], 700), (games[1], 400), (games[1], 800), (games[2], 500) })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: g,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.ObjEventSourceKey("DEATH", t),
                StartTimeSeconds: t - PatternConstants.MomentLeadSeconds,
                EndTimeSeconds: t + PatternConstants.MomentTrailSeconds,
                Title: PatternConstants.TokenLabel("DEATH"),
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }
        return games;
    }

    [Fact]
    public async Task BuildAsync_StartLessMoments_RenderWithoutFabricatedTime()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedFailingCriterionAsync(scope);

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindObjectiveCriteria);
        Assert.Equal(3, card.MomentCount);
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
    public async Task BuildAsync_PrunedVodFile_DegradesToNoVod_InsteadOfABrokenPlayer()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var games = await SeedTrackedDeathsAsync(scope);

        // The oldest game's vod_files row outlived its recording (Ascent
        // retention deleted the file); a fresh game's recording exists.
        var realVod = Path.Combine(Path.GetTempPath(), $"revu-test-vod-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(realVod, new byte[] { 0 });
        try
        {
            await scope.Vod.LinkVodAsync(games[2], Path.Combine(Path.GetTempPath(), "definitely-deleted.mp4"));
            await scope.Vod.LinkVodAsync(games[0], realVod);

            var snapshot = await Builder(scope).BuildAsync();
            var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindObjectiveEvents);

            foreach (var m in card.Moments.Where(m => m.GameId == games[2]))
            {
                Assert.False(m.HasVod);
                Assert.Equal("", m.VodPath);
            }
            foreach (var m in card.Moments.Where(m => m.GameId == games[0]))
            {
                Assert.True(m.HasVod);
                Assert.Equal(realVod, m.VodPath);
            }
        }
        finally
        {
            File.Delete(realVod);
        }
    }

    [Fact]
    public async Task BuildAsync_ReviewedPattern_ReArmsThroughTheSharedGate()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await SeedFailingCriterionAsync(scope);
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
        await scope.InitializeAsync();

        // A HIGH-severity criterion card, marked reviewed, and a MEDIUM-severity
        // tracked-deaths card left pending. Severity order alone would lead
        // with the reviewed card; the pending-first cap must not.
        var objectiveId = await SeedFailingCriterionAsync(scope);
        await SeedTrackedDeathsAsync(scope, firstGameId: 6801);
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
