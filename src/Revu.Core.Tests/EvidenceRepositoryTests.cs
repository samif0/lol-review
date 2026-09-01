using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Tests;

public sealed class EvidenceRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_CreatesUpdatesLinksAndDismissesEvidence()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", false);
        var objectiveId = await scope.Objectives.CreateAsync("Hold wave before objective", "macro");

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "lost-dragon-900-940",
            StartTimeSeconds: 900,
            EndTimeSeconds: 940,
            Title: "Lost Dragon fight"));

        await scope.Evidence.UpdateObjectiveAsync(evidenceId, objectiveId);
        await scope.Evidence.UpdatePolarityAsync(evidenceId, EvidencePolarities.Bad);
        await scope.Evidence.UpdateStatusAsync(evidenceId, EvidenceStatuses.Evidence);

        var rows = await scope.Evidence.GetForGameAsync(gameId);
        Assert.Single(rows);
        Assert.Equal(objectiveId, rows[0].ObjectiveId);
        Assert.Equal("Hold wave before objective", rows[0].ObjectiveTitle);
        Assert.Equal(EvidencePolarities.Bad, rows[0].Polarity);
        Assert.Equal(EvidenceStatuses.Evidence, rows[0].Status);

        await scope.Evidence.UpdateStatusAsync(evidenceId, EvidenceStatuses.Dismissed);
        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
    }

    [Fact]
    public async Task AttachingClipsToObjective_AddsTwoPointsEach_AndDoesNotDoubleCount()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");

        async Task<int> ScoreAsync()
        {
            var o = await scope.Objectives.GetAsync(objectiveId);
            return o!.Score;
        }

        Assert.Equal(0, await ScoreAsync());

        // Tag an existing evidence item onto the objective → +2.
        var firstId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "moment-1",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Missed roam"));
        await scope.Evidence.UpdateObjectiveAsync(firstId, objectiveId);
        Assert.Equal(2, await ScoreAsync());

        // Re-tagging the SAME objective on the same item must not stack points.
        await scope.Evidence.UpdateObjectiveAsync(firstId, objectiveId);
        Assert.Equal(2, await ScoreAsync());

        // A second clip created already attached to the objective → +2 more.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 7,
            SourceKey: "clip:7",
            StartTimeSeconds: 200,
            EndTimeSeconds: 220,
            Title: "Good roam",
            ObjectiveId: objectiveId,
            Status: EvidenceStatuses.Evidence));
        Assert.Equal(4, await ScoreAsync());

        // Re-upserting that same clip (same source key) must not award again.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 7,
            SourceKey: "clip:7",
            StartTimeSeconds: 201,
            EndTimeSeconds: 221,
            Title: "Good roam (renamed)",
            ObjectiveId: objectiveId,
            Status: EvidenceStatuses.Evidence));
        Assert.Equal(4, await ScoreAsync());
    }

    [Fact]
    public async Task UpsertAsync_WithSameSourceKey_ReusesCandidateWithoutClobberingUserStatus()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Kai'Sa", true);
        var firstId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 10,
            SourceKey: "clip:10",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Saved clip",
            Status: EvidenceStatuses.NeedsReview));

        await scope.Evidence.UpdateStatusAsync(firstId, EvidenceStatuses.Highlight);

        var secondId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 10,
            SourceKey: "clip:10",
            StartTimeSeconds: 101,
            EndTimeSeconds: 121,
            Title: "Saved clip renamed",
            Status: EvidenceStatuses.NeedsReview));

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(firstId, secondId);
        Assert.Equal(EvidenceStatuses.Highlight, row.Status);
        Assert.Equal("Saved clip renamed", row.Title);
    }

    [Fact]
    public async Task PromptId_RoundTripsThroughUpsertAndUpdate_IndependentlyOfObjective()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Did you ping before roaming?", 0);
        var otherPromptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Was the wave pushing?", 1);

        // Upsert a clip already tagged to BOTH an objective and a prompt.
        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 5,
            SourceKey: "clip:5",
            StartTimeSeconds: 300,
            EndTimeSeconds: 320,
            Title: "Roam clip",
            ObjectiveId: objectiveId,
            PromptId: promptId,
            Status: EvidenceStatuses.Evidence));

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(promptId, row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);

        // Re-tag to a different prompt — objective must stay untouched.
        await scope.Evidence.UpdatePromptAsync(evidenceId, otherPromptId);
        row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(otherPromptId, row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);

        // Detach the prompt (null) — objective still coexists.
        await scope.Evidence.UpdatePromptAsync(evidenceId, null);
        row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Null(row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);

        // Untagged-by-default: an upsert with no PromptId leaves it null.
        var untaggedId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "auto-moment-1",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Auto moment"));
        var untagged = (await scope.Evidence.GetForGameAsync(gameId))
            .Single(r => r.Id == untaggedId);
        Assert.Null(untagged.PromptId);
        Assert.Null(untagged.ObjectiveId);
    }

    [Fact]
    public async Task UpdatePromptAsync_DoesNotAwardObjectiveScore()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Did you ping?", 0);

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 9,
            SourceKey: "clip:9",
            StartTimeSeconds: 150,
            EndTimeSeconds: 170,
            Title: "Some clip"));

        // Prompt tagging is organizational only — score stays at 0 (objective_id
        // is the score-bearing path, exercised by the score test above).
        await scope.Evidence.UpdatePromptAsync(evidenceId, promptId);
        var obj = await scope.Objectives.GetAsync(objectiveId);
        Assert.Equal(0, obj!.Score);
    }
    // ── Pattern detectors (v3.6: objective-driven only) ─────────────────────

    private static async Task<long> SeedRankedGameAsync(
        TestDatabaseScope scope, long gameId, long ageSeconds, string champion = "Ahri", bool win = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId, champion: champion, win: win, timestamp: now - ageSeconds));
        return gameId;
    }

    private static EvidenceUpsert ObjEventAnchor(long gameId, string token, int timeS) => new(
        GameId: gameId,
        SourceKind: EvidenceKinds.TimelineRegion,
        SourceId: null,
        SourceKey: PatternConstants.ObjEventSourceKey(token, timeS),
        StartTimeSeconds: timeS - PatternConstants.MomentLeadSeconds,
        EndTimeSeconds: timeS + PatternConstants.MomentTrailSeconds,
        Title: PatternConstants.TokenLabel(token),
        Polarity: EvidencePolarities.Bad,
        Status: EvidenceStatuses.Evidence);

    private static EvidenceUpsert ObjCritAnchor(long gameId, long objectiveId, string objectiveTitle) => new(
        GameId: gameId,
        SourceKind: EvidenceKinds.TimelineRegion,
        SourceId: null,
        SourceKey: PatternConstants.ObjCritSourceKey(objectiveId),
        StartTimeSeconds: null,
        EndTimeSeconds: null,
        Title: PatternConstants.ObjCritTitle(objectiveTitle),
        Polarity: EvidencePolarities.Bad,
        Status: EvidenceStatuses.Evidence);

    [Fact]
    public async Task ObjectiveEvents_FireAtThreshold_AndDieWhenTheTokenIsUntracked()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Punish ganks with vision", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "DEATH" });

        var games = new long[3];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = await SeedRankedGameAsync(scope, 9201 + i, ageSeconds: (i + 1) * 86_400);
        }

        // 5 tracked-death anchors across 3 games — the exact threshold.
        await scope.Evidence.UpsertAsync(ObjEventAnchor(games[0], "DEATH", 300));
        await scope.Evidence.UpsertAsync(ObjEventAnchor(games[0], "DEATH", 700));
        await scope.Evidence.UpsertAsync(ObjEventAnchor(games[1], "DEATH", 400));
        await scope.Evidence.UpsertAsync(ObjEventAnchor(games[1], "DEATH", 900));
        await scope.Evidence.UpsertAsync(ObjEventAnchor(games[2], "DEATH", 500));

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveEvents);
        Assert.Equal(objectiveId, card.ObjectiveId);
        Assert.Equal("DEATH", card.Discriminator);
        Assert.Equal($"objective_events:obj{objectiveId}:DEATH", card.PatternKey);
        Assert.Equal(5, card.MomentCount);
        Assert.Equal(3, card.GameCount);

        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(card.MomentCount, moments.Count);
        Assert.All(moments, m => Assert.Equal("Death", m.Title));

        // Untracking the token kills card AND playlist (live-tie EXISTS).
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, Array.Empty<string>());
        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveEvents);
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(card));
    }

    [Fact]
    public async Task ObjectiveEvents_BelowGameSpread_DoesNotFire()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Track deaths", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "DEATH" });

        // 5 anchors but only 2 games — cross-game spread requirement fails.
        var gameA = await SeedRankedGameAsync(scope, 9211, ageSeconds: 86_400);
        var gameB = await SeedRankedGameAsync(scope, 9212, ageSeconds: 2 * 86_400);
        foreach (var (g, t) in new[] { (gameA, 100), (gameA, 200), (gameA, 300), (gameB, 150), (gameB, 250) })
        {
            await scope.Evidence.UpsertAsync(ObjEventAnchor(g, "DEATH", t));
        }

        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveEvents);
    }

    [Fact]
    public async Task ObjectiveEvents_PromotedClipMoment_StaysInCountAndPlaylist()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Track ganks", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "JUNGLE_GANK" });

        var games = new long[3];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = await SeedRankedGameAsync(scope, 9221 + i, ageSeconds: (i + 1) * 3600);
        }
        var ids = new List<long>();
        foreach (var (g, t) in new[] { (games[0], 300), (games[0], 700), (games[1], 400), (games[1], 800), (games[2], 500) })
        {
            ids.Add(await scope.Evidence.UpsertAsync(ObjEventAnchor(g, "JUNGLE_GANK", t)));
        }

        // Promote one moment to a clip (the note flow's silent clip-keep):
        // source key is rewritten, but its token-label title and exact window
        // survive, and the OR-branch counts it back in.
        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            games[0], 294, "noted", clipStartSeconds: 294, clipEndSeconds: 308, clipPath: @"C:\clips\g.mp4");
        await scope.Evidence.AttachClipToEvidenceAsync(ids[0], bookmarkId, 294, 308);

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveEvents);
        Assert.Equal(5, card.MomentCount);
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(5, moments.Count);
        Assert.Contains(moments, m => m.EvidenceId == ids[0] && m.SourceKind == EvidenceKinds.Clip);
    }

    [Fact]
    public async Task ObjectiveCriteria_FiresOnFailShare_AndHealsWhenAGamePasses()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("CS 7+/min by 10", "laning");

        var games = new long[4];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = await SeedRankedGameAsync(scope, 9231 + i, ageSeconds: (i + 1) * 86_400);
            await scope.Objectives.RecordGameAsync(games[i], objectiveId, practiced: true);
        }

        // Criterion evaluated on all 4; failed on 3 (share 75% → high severity).
        await scope.Objectives.SetCriteriaMetAsync(games[0], objectiveId, met: false);
        await scope.Objectives.SetCriteriaMetAsync(games[1], objectiveId, met: false);
        await scope.Objectives.SetCriteriaMetAsync(games[2], objectiveId, met: false);
        await scope.Objectives.SetCriteriaMetAsync(games[3], objectiveId, met: true);
        foreach (var g in games.Take(3))
        {
            await scope.Evidence.UpsertAsync(ObjCritAnchor(g, objectiveId, "CS 7+/min by 10"));
        }

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveCriteria);
        Assert.Equal(objectiveId, card.ObjectiveId);
        Assert.Equal($"objective_criteria:obj{objectiveId}", card.PatternKey);
        Assert.Equal(3, card.MomentCount);
        Assert.Equal("high", card.Severity);

        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(card.MomentCount, moments.Count);
        Assert.All(moments, m => Assert.Null(m.StartTimeSeconds));

        // A re-evaluation that passes drops that game from count AND playlist
        // via the live game_objectives EXISTS → below the 3-fail threshold.
        await scope.Objectives.SetCriteriaMetAsync(games[0], objectiveId, met: true);
        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveCriteria);
        Assert.Equal(2, (await scope.Evidence.GetPatternMomentsAsync(card)).Count);
    }

    [Fact]
    public async Task GetPatternCardsAsync_CountsEvidenceFromReviewedAndSkippedGames()
    {
        // THE regression pin from the v3.5 overhaul, still binding: the app's
        // own review flow must never hide a game's evidence from detection.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Stay in line with support", "laning");
        var reviewedGame = await SeedRankedGameAsync(scope, 9241, ageSeconds: 86_400, champion: "Kai'Sa");
        var skippedGame = await SeedRankedGameAsync(scope, 9242, ageSeconds: 2 * 86_400, champion: "Kai'Sa");

        foreach (var (gameId, offset) in new[] { (reviewedGame, 0), (skippedGame, 100) })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.Clip,
                SourceId: offset + 1,
                SourceKey: $"clip:{offset + 1}",
                StartTimeSeconds: 40 + offset,
                EndTimeSeconds: 55 + offset,
                Title: "Bad spacing with support",
                ObjectiveId: objectiveId,
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }

        await scope.Games.UpdateReviewAsync(reviewedGame, new GameReview
        {
            Rating = 4,
            Notes = "Reviewed the lane spacing clips."
        });
        await scope.SessionLog.LogGameAsync(skippedGame, "Kai'Sa", win: false, mentalRating: 5);
        await scope.SessionLog.MarkSkippedAsync(skippedGame);

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindBadObjectiveEvidence && c.ObjectiveId == objectiveId);
        Assert.Equal(2, card.MomentCount);
        Assert.Equal(2, (await scope.Evidence.GetPatternMomentsAsync(card)).Count);
    }

    [Fact]
    public async Task GetPatternCardsAsync_WindowExcludesOldGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Track deaths", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "DEATH" });

        var recentA = await SeedRankedGameAsync(scope, 9251, ageSeconds: 86_400);
        var recentB = await SeedRankedGameAsync(scope, 9252, ageSeconds: 2 * 86_400);
        var stale = await SeedRankedGameAsync(scope, 9253, ageSeconds: (PatternConstants.WindowDays + 5) * 86_400L);

        // 4 recent anchors over 2 games + 3 stale ones: only the recent set is
        // countable, and it misses both the 5-count and 3-game thresholds.
        foreach (var (g, t) in new[] { (recentA, 100), (recentA, 200), (recentB, 150), (recentB, 250), (stale, 100), (stale, 200), (stale, 300) })
        {
            await scope.Evidence.UpsertAsync(ObjEventAnchor(g, "DEATH", t));
        }

        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindObjectiveEvents);
    }

    [Fact]
    public async Task GetPatternCardsAsync_EveryCardsMomentCount_MatchesItsPlaylist()
    {
        // Anti-drift invariant: whatever detectors fire, the card's MomentCount
        // must equal the playlist GetPatternMomentsAsync resolves for it.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var eventsObjective = await scope.Objectives.CreateAsync("Track ganks", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(eventsObjective, new[] { "JUNGLE_GANK" });
        var clipsObjective = await scope.Objectives.CreateAsync("Reset before dragon", "macro");
        var critObjective = await scope.Objectives.CreateAsync("CS 7+/min by 10", "laning");

        var games = new long[4];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = await SeedRankedGameAsync(scope, 9261 + i, ageSeconds: (i + 1) * 3600);
            await scope.Objectives.RecordGameAsync(games[i], critObjective, practiced: true);
            await scope.Objectives.SetCriteriaMetAsync(games[i], critObjective, met: false);
            await scope.Evidence.UpsertAsync(ObjCritAnchor(games[i], critObjective, "CS 7+/min by 10"));
        }

        foreach (var (g, t) in new[] { (games[0], 300), (games[0], 700), (games[1], 400), (games[2], 500), (games[3], 600) })
        {
            await scope.Evidence.UpsertAsync(ObjEventAnchor(g, "JUNGLE_GANK", t));
        }

        // A dismissed anchor must leave BOTH count and playlist.
        var dismissed = await scope.Evidence.UpsertAsync(ObjEventAnchor(games[1], "JUNGLE_GANK", 900));
        await scope.Evidence.UpdateStatusAsync(dismissed, EvidenceStatuses.Dismissed);

        // bad_objective_evidence with mixed polarity: count/list bad rows only.
        foreach (var (key, polarity) in new[] { ("bo:1", EvidencePolarities.Bad), ("bo:2", EvidencePolarities.Bad), ("bo:3", EvidencePolarities.Good) })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: games[0],
                SourceKind: EvidenceKinds.Clip,
                SourceId: null,
                SourceKey: key,
                StartTimeSeconds: 50,
                EndTimeSeconds: 60,
                Title: "clip",
                ObjectiveId: clipsObjective,
                Polarity: polarity,
                Status: EvidenceStatuses.Evidence));
        }

        var cards = await scope.Evidence.GetPatternCardsAsync();
        Assert.Equal(3, cards.Select(c => c.Kind).Distinct().Count());
        foreach (var card in cards)
        {
            var moments = await scope.Evidence.GetPatternMomentsAsync(card);
            Assert.Equal(card.MomentCount, moments.Count);
        }
    }
}
