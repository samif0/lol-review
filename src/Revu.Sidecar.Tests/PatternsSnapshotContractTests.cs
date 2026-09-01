using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract tests for the overhauled GET /api/patterns snapshot and the
/// classify→materialize write sequence the death-audit endpoints run:
///   • a backend failure surfaces as errorText (never a clean "no patterns yet")
///   • start-less game-level moments render without a fabricated 0:00
///   • reviewed patterns re-arm through the shared PatternReviewGate watermark
///   • /api/death/classify + /clear keep the audit evidence ledger in step
/// </summary>
public sealed class PatternsSnapshotContractTests
{
    private static PatternEvidenceMaterializer Materializer(SidecarWriteScope scope) => new(
        new GameEventsRepository(scope.ConnectionFactory),
        scope.Evidence,
        scope.DeathClassifications,
        scope.ConceptTags,
        scope.SessionLog,
        scope.Games,
        NullLogger<PatternEvidenceMaterializer>.Instance);

    private static PatternsSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Evidence,
        NullLogger<PatternsSnapshotBuilder>.Instance);

    /// <summary>Seed the rule_breaks pattern at its threshold: three recent
    /// rule-broken games with their materialized anchors.</summary>
    private static async Task<long[]> SeedRuleBreaksAsync(SidecarWriteScope scope)
    {
        var materializer = Materializer(scope);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new long[3];
        for (var i = 0; i < games.Length; i++)
        {
            var game = await scope.SeedGameAsync(gameId: 6401 + i, timestamp: now - (i + 1) * 3600);
            games[i] = game.GameId;
            await scope.SessionLog.LogGameAsync(game.GameId, "Ahri", win: false, mentalRating: 5);
            await scope.SessionLog.SetRuleBrokenAsync(game.GameId, true);
            await materializer.MaterializeReviewSignalsAsync(game.GameId);
        }
        return games;
    }

    [Fact]
    public async Task DeathClassifyAndClear_KeepTheAuditEvidenceLedgerInStep()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var materializer = Materializer(scope);

        var game = await scope.SeedGameAsync(gameId: 6301);

        // The exact sequence POST /api/death/classify runs.
        await scope.DeathClassifications.UpsertAsync(game.GameId, 312, DeathClasses.Vision);
        await materializer.UpsertClassifiedDeathAsync(game.GameId, 312, DeathClasses.Vision);

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(game.GameId, includeDismissed: true));
        Assert.Equal("Death: VISION", row.Title);
        Assert.Equal(PatternConstants.DeathAuditSourceKey(312), row.SourceKey);
        Assert.Equal(EvidenceStatuses.Evidence, row.Status);

        // The exact sequence POST /api/death/clear runs.
        await scope.DeathClassifications.ClearAsync(game.GameId, 312);
        await materializer.ClearClassifiedDeathAsync(game.GameId, 312);

        Assert.Empty(await scope.Evidence.GetForGameAsync(game.GameId, includeDismissed: true));
    }

    [Fact]
    public async Task BuildAsync_StartLessMoments_RenderWithoutFabricatedTime()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedRuleBreaksAsync(scope);

        var snapshot = await Builder(scope).BuildAsync();

        var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindRuleBreaks);
        Assert.Equal(3, card.MomentCount);
        Assert.All(card.Moments, m =>
        {
            Assert.Null(m.StartTimeSeconds);
            Assert.Equal("", m.TimeLabel);
            // No trailing "· 0:00" — the header is champion · result only.
            Assert.DoesNotContain("0:00", m.VideoHeaderText);
        });
        Assert.Equal("", snapshot.ErrorText);
        Assert.Equal(PatternConstants.WindowDays, snapshot.WindowDays);
    }

    [Fact]
    public async Task BuildAsync_ReviewedPattern_ReArmsThroughTheSharedGate()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedRuleBreaksAsync(scope);

        // Mark the pattern reviewed → it closes.
        await scope.Evidence.MarkPatternReviewedAsync(
            PatternConstants.KindRuleBreaks, PatternConstants.KindRuleBreaks, 3);
        var closed = await Builder(scope).BuildAsync();
        var closedCard = Assert.Single(closed.Patterns, p => p.Kind == PatternConstants.KindRuleBreaks);
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
        var reArmedCard = Assert.Single(reArmed.Patterns, p => p.Kind == PatternConstants.KindRuleBreaks);
        Assert.False(reArmedCard.IsReviewed);
        Assert.Equal(3, reArmedCard.NewMomentCount);
        Assert.True(reArmed.HasPending);
    }

    [Fact]
    public async Task BuildAsync_PendingCardsRankAheadOfReviewedOnes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var games = await SeedRuleBreaksAsync(scope);

        // Also arm gank_deaths (severity HIGH — the repo orders it ahead of the
        // medium rule_breaks card) and mark IT reviewed.
        foreach (var (game, t) in new[] { (games[0], 300), (games[0], 700), (games[1], 400) })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: game,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.GankDeathSourceKey(t),
                StartTimeSeconds: t - PatternConstants.DeathMomentLeadSeconds,
                EndTimeSeconds: t + PatternConstants.DeathMomentTrailSeconds,
                Title: PatternConstants.GankDeathTitle,
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }
        await scope.Evidence.MarkPatternReviewedAsync(
            PatternConstants.KindGankDeaths, PatternConstants.KindGankDeaths, 3);

        var snapshot = await Builder(scope).BuildAsync();

        // The pending rule_breaks card leads despite lower severity; the
        // reviewed gank card follows instead of consuming a leading slot.
        Assert.Equal(2, snapshot.Patterns.Count);
        Assert.Equal(PatternConstants.KindRuleBreaks, snapshot.Patterns[0].Kind);
        Assert.False(snapshot.Patterns[0].IsReviewed);
        Assert.Equal(PatternConstants.KindGankDeaths, snapshot.Patterns[1].Kind);
        Assert.True(snapshot.Patterns[1].IsReviewed);
        Assert.Equal(1, snapshot.PendingCount);
    }

    [Fact]
    public async Task BuildAsync_PrunedVodFile_DegradesToNoVod_InsteadOfABrokenPlayer()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prunedGame = (await scope.SeedGameAsync(gameId: 6501, timestamp: now - 7200)).GameId;
        var freshGame = (await scope.SeedGameAsync(gameId: 6502, timestamp: now - 3600)).GameId;

        // The pruned game's vod_files row outlived its recording (Ascent
        // retention deleted the file); the fresh game's recording exists.
        var realVod = Path.Combine(Path.GetTempPath(), $"revu-test-vod-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(realVod, new byte[] { 0 });
        try
        {
            await scope.Vod.LinkVodAsync(prunedGame, Path.Combine(Path.GetTempPath(), "definitely-deleted.mp4"));
            await scope.Vod.LinkVodAsync(freshGame, realVod);

            foreach (var (game, t) in new[] { (prunedGame, 300), (prunedGame, 700), (freshGame, 400) })
            {
                await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                    GameId: game,
                    SourceKind: EvidenceKinds.TimelineRegion,
                    SourceId: null,
                    SourceKey: PatternConstants.GankDeathSourceKey(t),
                    StartTimeSeconds: t - PatternConstants.DeathMomentLeadSeconds,
                    EndTimeSeconds: t + PatternConstants.DeathMomentTrailSeconds,
                    Title: PatternConstants.GankDeathTitle,
                    Polarity: EvidencePolarities.Bad,
                    Status: EvidenceStatuses.Evidence));
            }

            var snapshot = await Builder(scope).BuildAsync();
            var card = Assert.Single(snapshot.Patterns, p => p.Kind == PatternConstants.KindGankDeaths);

            // Missing recording → the graceful no-VOD state (moment still
            // listed, notes still work, no player that errors on load).
            foreach (var m in card.Moments.Where(m => m.GameId == prunedGame))
            {
                Assert.False(m.HasVod);
                Assert.Equal("", m.VodPath);
            }
            var playable = Assert.Single(card.Moments, m => m.GameId == freshGame);
            Assert.True(playable.HasVod);
            Assert.Equal(realVod, playable.VodPath);
        }
        finally
        {
            File.Delete(realVod);
        }
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
        // The old copy told users to "keep tagging evidence" while the backend
        // discarded evidence from reviewed games — the new copy must name the
        // signals that actually feed detection.
        Assert.Contains("review", snapshot.EmptyText);
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
        public Task<long?> FindPromotedDeathAuditAsync(long gameId, int gameTimeSeconds) => throw Boom();
        public Task<long?> FindPromotedTwinAsync(long gameId, string title, int startTimeSeconds, int endTimeSeconds) => throw Boom();
        public Task UpdateTitleAsync(long evidenceId, string title) => throw Boom();
        public Task<int> DeleteBySourceKeyAsync(long gameId, string sourceKind, string sourceKey) => throw Boom();
    }
}
