using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The Pattern Review viewer resolves a pattern card into the ordered cross-game
/// moments that compose it, and tracks which patterns have been reviewed with a
/// re-arm watermark (PatternReviewGate) so a reviewed pattern comes back only
/// when genuinely new moments accrue.
/// </summary>
public sealed class PatternReviewTests
{
    [Fact]
    public async Task GetPatternMoments_GankDeaths_ReturnsOrderedCrossGameMomentsWithVodPaths()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Two recent ranked games (explicit timestamps for deterministic
        // ordering — both inside the pattern window), each with a matched VOD.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long olderGame = 5001;
        const long newerGame = 5002;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(olderGame, champion: "Ahri", win: false, timestamp: now - 3600));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(newerGame, champion: "Kai'Sa", win: true, timestamp: now - 60));
        await scope.Vod.LinkVodAsync(olderGame, @"C:\vods\older.mp4");
        await scope.Vod.LinkVodAsync(newerGame, @"C:\vods\newer.mp4");

        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: newerGame, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: PatternConstants.GankDeathSourceKey(600), StartTimeSeconds: 600, EndTimeSeconds: 605,
            Title: PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: olderGame, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: PatternConstants.GankDeathSourceKey(900), StartTimeSeconds: 900, EndTimeSeconds: 905,
            Title: PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: olderGame, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: PatternConstants.GankDeathSourceKey(300), StartTimeSeconds: 300, EndTimeSeconds: 305,
            Title: PatternConstants.GankDeathTitle));

        var card = new ObjectivePatternCard(
            Kind: PatternConstants.KindGankDeaths, Title: "Dying to jungle ganks", Detail: "", Severity: "high");
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);

        Assert.Equal(3, moments.Count);

        // Ordered oldest game first, then by in-game time within a game.
        Assert.Equal(olderGame, moments[0].GameId);
        Assert.Equal(300, moments[0].StartTimeSeconds);
        Assert.Equal(olderGame, moments[1].GameId);
        Assert.Equal(900, moments[1].StartTimeSeconds);
        Assert.Equal(newerGame, moments[2].GameId);

        // VOD path + game metadata are joined onto each moment; created_at is
        // populated (the re-arm watermark input).
        Assert.Equal(@"C:\vods\older.mp4", moments[0].VodPath);
        Assert.Equal("Ahri", moments[0].ChampionName);
        Assert.False(moments[0].Win);
        Assert.Equal(@"C:\vods\newer.mp4", moments[2].VodPath);
        Assert.True(moments[2].Win);
        Assert.All(moments, m => Assert.True(m.CreatedAt > 0));
    }

    [Fact]
    public async Task GetPatternMoments_ExcludesDismissed_ButNotReviewedGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var game = await scope.Games.SaveManualAsync("Ahri", win: false);

        var keptId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "keep", StartTimeSeconds: 100, EndTimeSeconds: 105,
            Title: PatternConstants.GankDeathTitle));
        var dismissedId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "drop", StartTimeSeconds: 200, EndTimeSeconds: 205,
            Title: PatternConstants.GankDeathTitle));
        await scope.Evidence.UpdateStatusAsync(dismissedId, EvidenceStatuses.Dismissed);

        // Fully review the game — the moment must STAY listed. (The old contract
        // hid all evidence from reviewed games, which starved every pattern for
        // anyone who actually used the review flow.)
        await scope.Games.UpdateReviewAsync(game, new Revu.Core.Models.GameReview
        {
            Rating = 4,
            Notes = "Reviewed it."
        });

        var card = new ObjectivePatternCard(Kind: PatternConstants.KindGankDeaths, Title: "x", Detail: "");
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
        Assert.Empty(await scope.Evidence.GetReviewedPatternsAsync());

        await scope.Evidence.MarkPatternReviewedAsync("gank_deaths", "gank_deaths", 5);
        await scope.Evidence.MarkPatternReviewedAsync("bad_objective_evidence:obj7", "bad_objective_evidence", 3);

        Assert.Equal(2, await scope.Evidence.CountReviewedPatternsAsync());

        // Re-reviewing the same key updates in place — no duplicate — and
        // refreshes the reviewed_at watermark.
        var before = (await scope.Evidence.GetReviewedPatternsAsync())["gank_deaths"];
        await Task.Delay(1100);
        await scope.Evidence.MarkPatternReviewedAsync("gank_deaths", "gank_deaths", 8);
        Assert.Equal(2, await scope.Evidence.CountReviewedPatternsAsync());

        var stamps = await scope.Evidence.GetReviewedPatternsAsync();
        Assert.True(stamps.ContainsKey("gank_deaths"));
        Assert.True(stamps.ContainsKey("bad_objective_evidence:obj7"));
        Assert.True(stamps["gank_deaths"] > before);
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

        // Still ONE row (promoted in place, not duplicated), now clip-backed —
        // and the TITLE is preserved, which is what keeps a promoted moment
        // inside its pattern playlist (detection keys off title).
        var row = Assert.Single(rows);
        Assert.Equal(evidenceId, row.Id);
        Assert.Equal(EvidenceKinds.Clip, row.SourceKind);
        Assert.Equal(bookmarkId, row.SourceId);
        Assert.Equal(EvidenceStatuses.Evidence, row.Status); // promoted out of needs-review
        Assert.Equal(372, row.StartTimeSeconds);
        Assert.Equal(384, row.EndTimeSeconds);
        Assert.Equal("Death", row.Title);
    }

    [Fact]
    public void PatternKey_DistinguishesObjectiveScopedAndDiscriminatedPatterns()
    {
        var global = new ObjectivePatternCard(Kind: PatternConstants.KindGankDeaths, Title: "x", Detail: "");
        var objA = new ObjectivePatternCard(Kind: PatternConstants.KindBadObjectiveEvidence, Title: "x", Detail: "", ObjectiveId: 1);
        var objB = new ObjectivePatternCard(Kind: PatternConstants.KindBadObjectiveEvidence, Title: "x", Detail: "", ObjectiveId: 2);
        var greed = new ObjectivePatternCard(Kind: PatternConstants.KindDeathClassMix, Title: "x", Detail: "", Discriminator: "greed");
        var vision = new ObjectivePatternCard(Kind: PatternConstants.KindDeathClassMix, Title: "x", Detail: "", Discriminator: "vision");

        Assert.Equal("gank_deaths", global.PatternKey);
        Assert.NotEqual(objA.PatternKey, objB.PatternKey);
        Assert.Equal("death_class_mix:greed", greed.PatternKey);
        Assert.NotEqual(greed.PatternKey, vision.PatternKey);
    }

    // ── PatternReviewGate: the reviewed/re-arm rule ─────────────────────────

    private static PatternMoment Moment(long createdAt) => new(
        EvidenceId: createdAt, GameId: 1, ChampionName: "Ahri", Win: false,
        GameTimestamp: 0, StartTimeSeconds: 10, EndTimeSeconds: 20,
        Title: "t", Note: "", Polarity: "bad", SourceKind: "timeline_region",
        VodPath: "", CreatedAt: createdAt);

    [Fact]
    public void PatternReviewGate_UnreviewedPattern_IsPending()
    {
        var stamps = new Dictionary<string, long>();
        var moments = new[] { Moment(100), Moment(200) };
        Assert.False(PatternReviewGate.IsReviewed(stamps, "gank_deaths", moments));
        Assert.Equal(0, PatternReviewGate.NewMomentCount(stamps, "gank_deaths", moments));
    }

    [Fact]
    public void PatternReviewGate_ReviewedPattern_StaysClosedUntilTwoNewMoments()
    {
        var stamps = new Dictionary<string, long> { ["gank_deaths"] = 1000 };

        // All moments predate the review → closed.
        Assert.True(PatternReviewGate.IsReviewed(stamps, "gank_deaths", new[] { Moment(900), Moment(950) }));

        // ONE new moment → still closed (hysteresis: no re-nag treadmill).
        Assert.True(PatternReviewGate.IsReviewed(stamps, "gank_deaths", new[] { Moment(900), Moment(1500) }));

        // TWO new moments → re-armed, and the new count is surfaced.
        var reArmed = new[] { Moment(900), Moment(1500), Moment(1600) };
        Assert.False(PatternReviewGate.IsReviewed(stamps, "gank_deaths", reArmed));
        Assert.Equal(2, PatternReviewGate.NewMomentCount(stamps, "gank_deaths", reArmed));
    }

    [Fact]
    public void PatternReviewGate_LegacyStamp_ReArmsOnFreshMoments()
    {
        // A months-old review stamp (e.g. from the pre-overhaul era) must not
        // suppress a pattern rebuilt from fresh moments — "born pre-reviewed
        // forever" is exactly the bug the watermark rule removes.
        var stamps = new Dictionary<string, long> { ["bad_objective_evidence:obj7"] = 1 };
        var fresh = new[] { Moment(5_000_000), Moment(5_000_100), Moment(5_000_200) };
        Assert.False(PatternReviewGate.IsReviewed(stamps, "bad_objective_evidence:obj7", fresh));
    }
}
