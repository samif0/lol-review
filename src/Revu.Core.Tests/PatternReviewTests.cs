using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Tests;

/// <summary>
/// The Pattern Review viewer resolves a dashboard pattern card into the ordered
/// cross-game moments that compose it, and tracks which patterns have been
/// reviewed (drives the dashboard "Patterns Reviewed" stat + nag suppression).
/// </summary>
public sealed class PatternReviewTests
{
    [Fact]
    public async Task GetPatternMoments_IsolatedDeaths_ReturnsOrderedCrossGameMomentsWithVodPaths()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Two unreviewed games (explicit ids + timestamps for deterministic
        // ordering), each with a matched VOD.
        const long olderGame = 5001;
        const long newerGame = 5002;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(olderGame, champion: "Ahri", win: false, timestamp: 1_000));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(newerGame, champion: "Kai'Sa", win: true, timestamp: 2_000));
        await scope.Vod.LinkVodAsync(olderGame, @"C:\vods\older.mp4");
        await scope.Vod.LinkVodAsync(newerGame, @"C:\vods\newer.mp4");

        // Three isolated-death evidence items — the pattern threshold.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: newerGame, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "d-new-1", StartTimeSeconds: 600, EndTimeSeconds: 605, Title: "Death"));
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: olderGame, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "d-old-1", StartTimeSeconds: 900, EndTimeSeconds: 905, Title: "Isolated death"));
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: olderGame, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "d-old-2", StartTimeSeconds: 300, EndTimeSeconds: 305, Title: "First death"));

        var card = new ObjectivePatternCard(
            Kind: "isolated_deaths", Title: "Frequent isolated deaths", Detail: "", Severity: "high");
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);

        Assert.Equal(3, moments.Count);

        // Ordered oldest game first, then by in-game time within a game.
        Assert.Equal(olderGame, moments[0].GameId);
        Assert.Equal(300, moments[0].StartTimeSeconds);
        Assert.Equal(olderGame, moments[1].GameId);
        Assert.Equal(900, moments[1].StartTimeSeconds);
        Assert.Equal(newerGame, moments[2].GameId);

        // VOD path + game metadata are joined onto each moment.
        Assert.Equal(@"C:\vods\older.mp4", moments[0].VodPath);
        Assert.Equal("Ahri", moments[0].ChampionName);
        Assert.False(moments[0].Win);
        Assert.Equal(@"C:\vods\newer.mp4", moments[2].VodPath);
        Assert.True(moments[2].Win);
    }

    [Fact]
    public async Task GetPatternMoments_ExcludesDismissedAndReviewedGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var game = await scope.Games.SaveManualAsync("Ahri", win: false);

        var keptId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "keep", StartTimeSeconds: 100, EndTimeSeconds: 105, Title: "Death"));
        var dismissedId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "drop", StartTimeSeconds: 200, EndTimeSeconds: 205, Title: "Death"));
        await scope.Evidence.UpdateStatusAsync(dismissedId, EvidenceStatuses.Dismissed);

        var card = new ObjectivePatternCard(Kind: "isolated_deaths", Title: "x", Detail: "");
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);

        var moment = Assert.Single(moments);
        Assert.Equal(keptId, moment.EvidenceId);
    }

    [Fact]
    public async Task MarkPatternReviewed_IsUpsertAndCountsDistinctPatterns()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        Assert.Equal(0, await scope.Evidence.CountReviewedPatternsAsync());
        Assert.Empty(await scope.Evidence.GetReviewedPatternKeysAsync());

        await scope.Evidence.MarkPatternReviewedAsync("isolated_deaths", "isolated_deaths", 5);
        await scope.Evidence.MarkPatternReviewedAsync("bad_objective_evidence:obj7", "bad_objective_evidence", 3);

        Assert.Equal(2, await scope.Evidence.CountReviewedPatternsAsync());

        // Re-reviewing the same key updates in place — no duplicate.
        await scope.Evidence.MarkPatternReviewedAsync("isolated_deaths", "isolated_deaths", 8);
        Assert.Equal(2, await scope.Evidence.CountReviewedPatternsAsync());

        var keys = await scope.Evidence.GetReviewedPatternKeysAsync();
        Assert.Contains("isolated_deaths", keys);
        Assert.Contains("bad_objective_evidence:obj7", keys);
    }

    [Fact]
    public async Task AttachClipToEvidence_PromotesMomentRowToClipInPlace_NoDuplicate()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", win: false);

        // An auto-detected death moment: timeline_region kind, needs_review.
        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "death-380", StartTimeSeconds: 380, EndTimeSeconds: 380, Title: "Death",
            Status: EvidenceStatuses.NeedsReview));

        // Simulate the auto-clip: a bookmark + promote the moment row to the clip.
        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId, 372, "ganked overextending", clipStartSeconds: 372, clipEndSeconds: 384,
            clipPath: @"C:\clips\ahri.mp4");
        await scope.Evidence.AttachClipToEvidenceAsync(evidenceId, bookmarkId, 372, 384);

        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);

        // Still ONE row (promoted in place, not duplicated), now clip-backed.
        var row = Assert.Single(rows);
        Assert.Equal(evidenceId, row.Id);
        Assert.Equal(EvidenceKinds.Clip, row.SourceKind);
        Assert.Equal(bookmarkId, row.SourceId);
        Assert.Equal(EvidenceStatuses.Evidence, row.Status); // promoted out of needs-review
        Assert.Equal(372, row.StartTimeSeconds);
        Assert.Equal(384, row.EndTimeSeconds);
    }

    [Fact]
    public void PatternKey_DistinguishesObjectiveScopedPatterns()
    {
        var global = new ObjectivePatternCard(Kind: "isolated_deaths", Title: "x", Detail: "");
        var objA = new ObjectivePatternCard(Kind: "bad_objective_evidence", Title: "x", Detail: "", ObjectiveId: 1);
        var objB = new ObjectivePatternCard(Kind: "bad_objective_evidence", Title: "x", Detail: "", ObjectiveId: 2);

        Assert.Equal("isolated_deaths", global.PatternKey);
        Assert.NotEqual(objA.PatternKey, objB.PatternKey);
    }
}
