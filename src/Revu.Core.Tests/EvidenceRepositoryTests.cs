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

    // ── Pattern detectors ───────────────────────────────────────────────────

    private static async Task<long> SeedRankedGameAsync(
        TestDatabaseScope scope, long gameId, long ageSeconds, string champion = "Ahri", bool win = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId, champion: champion, win: win, timestamp: now - ageSeconds));
        return gameId;
    }

    private static EvidenceUpsert TitledMoment(long gameId, string sourceKey, int startS, string title) => new(
        GameId: gameId,
        SourceKind: EvidenceKinds.TimelineRegion,
        SourceId: null,
        SourceKey: sourceKey,
        StartTimeSeconds: startS,
        EndTimeSeconds: startS + 10,
        Title: title,
        Polarity: EvidencePolarities.Bad,
        Status: EvidenceStatuses.Evidence);

    [Fact]
    public async Task GetPatternCardsAsync_FindsTitleKeyedKinds_AtTheirThresholds()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameA = await SeedRankedGameAsync(scope, 9101, ageSeconds: 3 * 86_400);
        var gameB = await SeedRankedGameAsync(scope, 9102, ageSeconds: 1 * 86_400, champion: "Kai'Sa");

        // gank_deaths: 3 moments across 2 games (its exact threshold).
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:300", 300, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:700", 700, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "gank-death:420", 420, PatternConstants.GankDeathTitle));

        // lost_objective_fights: 3 across 2 games.
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "objective:dragon:900:960", 900, "Lost Dragon fight"));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "objective:baron:1500:1560", 1500, "Lost Baron fight"));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "objective:dragon:800:860", 800, "Lost Dragon fight"));

        // deaths_before_objectives: 3 across 2 games.
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "objective-death:dragon:870:905", 870, "Death before Dragon"));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "objective-death:baron:1460:1505", 1460, "Death before Baron"));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "objective-death:dragon:760:805", 760, "Death before Dragon"));

        var cards = await scope.Evidence.GetPatternCardsAsync();

        var gank = Assert.Single(cards, c => c.Kind == PatternConstants.KindGankDeaths);
        Assert.Equal("high", gank.Severity);
        Assert.Equal(3, gank.MomentCount);
        Assert.Equal(2, gank.GameCount);

        Assert.Contains(cards, c => c.Kind == PatternConstants.KindLostObjectiveFights);
        Assert.Contains(cards, c => c.Kind == PatternConstants.KindDeathsBeforeObjectives);

        // 'Lost Teamfight' must never feed lost_objective_fights (no space
        // before 'fight' — the drift that silently killed the old detector).
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "combat:lost-teamfight:200:230", 200, "Lost Teamfight"));
        var again = Assert.Single(await scope.Evidence.GetPatternCardsAsync(), c => c.Kind == PatternConstants.KindLostObjectiveFights);
        Assert.Equal(3, again.MomentCount);
    }

    [Fact]
    public async Task GetPatternCardsAsync_BelowThreshold_OrSingleGame_DoesNotFire()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameA = await SeedRankedGameAsync(scope, 9111, ageSeconds: 86_400);

        // 3 gank deaths but all in ONE game → the cross-game requirement fails.
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:100", 100, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:200", 200, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:300", 300, PatternConstants.GankDeathTitle));

        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindGankDeaths);
    }

    [Fact]
    public async Task GetPatternCardsAsync_CountsEvidenceFromReviewedAndSkippedGames()
    {
        // THE regression pin for the overhaul: the app's own review flow
        // (SaveAsync hardwires Rating=1; skip stamps is_skipped=1) used to eject
        // a game's evidence from every pattern query — reviewing games, the
        // point of the app, guaranteed the Patterns page stayed empty.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Stay in line with support", "laning");
        var reviewedGame = await SeedRankedGameAsync(scope, 9121, ageSeconds: 86_400, champion: "Kai'Sa");
        var skippedGame = await SeedRankedGameAsync(scope, 9122, ageSeconds: 2 * 86_400, champion: "Kai'Sa");

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

        // Review one game (rating + text), skip the other.
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
        Assert.Equal(2, card.GameCount);

        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(2, moments.Count);
    }

    [Fact]
    public async Task GetPatternCardsAsync_WindowExcludesOldHiddenAndCasualGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var recent = await SeedRankedGameAsync(scope, 9131, ageSeconds: 86_400);
        var stale = await SeedRankedGameAsync(scope, 9132, ageSeconds: (PatternConstants.WindowDays + 5) * 86_400);

        // Two recent + two stale gank deaths: only the recent pair is countable,
        // which is below the 3-count threshold → no card. All-time counting
        // (the old behavior) would have fired on 4.
        await scope.Evidence.UpsertAsync(TitledMoment(recent, "gank-death:100", 100, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(recent, "gank-death:200", 200, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(stale, "gank-death:100", 100, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(stale, "gank-death:200", 200, PatternConstants.GankDeathTitle));

        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindGankDeaths);
    }

    [Fact]
    public async Task GetPatternCardsAsync_DeathClassMix_RequiresCountGamesAndShare()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameA = await SeedRankedGameAsync(scope, 9141, ageSeconds: 86_400);
        var gameB = await SeedRankedGameAsync(scope, 9142, ageSeconds: 2 * 86_400);

        var greedLabel = DeathClasses.LabelFor(DeathClasses.Greed);
        var greedTitle = PatternConstants.DeathAuditTitle(greedLabel);

        // 4 GREED audit moments across 2 games…
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "death-audit:100", 100, greedTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "death-audit:300", 300, greedTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "death-audit:200", 200, greedTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "death-audit:400", 400, greedTitle));

        // …with 6 classified deaths total in the window → share 4/6 = 67%.
        var deathClassifications = new DeathClassificationsRepository(scope.ConnectionFactory);
        await deathClassifications.UpsertAsync(gameA, 100, DeathClasses.Greed);
        await deathClassifications.UpsertAsync(gameA, 300, DeathClasses.Greed);
        await deathClassifications.UpsertAsync(gameB, 200, DeathClasses.Greed);
        await deathClassifications.UpsertAsync(gameB, 400, DeathClasses.Greed);
        await deathClassifications.UpsertAsync(gameA, 500, DeathClasses.Vision);
        await deathClassifications.UpsertAsync(gameB, 600, DeathClasses.Vision);

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindDeathClassMix);
        Assert.Equal(DeathClasses.Greed, card.Discriminator);
        Assert.Equal("death_class_mix:greed", card.PatternKey);
        Assert.Equal(4, card.MomentCount);
        // 67% of classified deaths ≥ the 50% high bar.
        Assert.Equal("high", card.Severity);

        // Its playlist matches the card exactly (title-keyed).
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(card.MomentCount, moments.Count);
        Assert.All(moments, m => Assert.Equal(greedTitle, m.Title));

        // Dilute the share below 30% (14 more non-greed classified deaths)
        // → the card stops firing even though the raw count still passes.
        for (var i = 0; i < 14; i++)
        {
            await deathClassifications.UpsertAsync(gameA, 1000 + i, DeathClasses.Tempo);
        }
        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindDeathClassMix);
    }

    [Fact]
    public async Task GetPatternCardsAsync_RecurringConceptTag_GatedOnLiveTagAndShare()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var games = new long[4];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = await SeedRankedGameAsync(scope, 9151 + i, ageSeconds: (i + 1) * 86_400);
            await scope.Games.UpdateReviewAsync(games[i], new GameReview { Rating = 3, Notes = "r" });
        }

        var tagId = await scope.ConceptTags.CreateAsync("Caught out again", "negative", "#ef4444");
        var positiveTagId = await scope.ConceptTags.CreateAsync("Won lane hard", "positive", "#22c55e");

        // Tag 3 of the 4 reviewed games (share 75%) with the negative tag; write
        // the matching per-game anchor rows (what the materializer produces).
        for (var i = 0; i < 3; i++)
        {
            await scope.ConceptTags.SetForGameAsync(games[i], new[] { tagId, positiveTagId });
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: games[i],
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.TagSourceKey(tagId),
                StartTimeSeconds: null,
                EndTimeSeconds: null,
                Title: "Caught out again",
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindRecurringConceptTag);
        Assert.Equal($"tag{tagId}", card.Discriminator);
        Assert.Equal(3, card.MomentCount);
        Assert.Equal("high", card.Severity); // 75% ≥ the 50% high bar

        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(card.MomentCount, moments.Count);
        Assert.All(moments, m => Assert.Null(m.StartTimeSeconds));

        // Untag one game: its anchor row remains, but the live-signal EXISTS
        // drops it from count AND playlist symmetrically → below the 3-count
        // threshold, the card stops firing.
        await scope.ConceptTags.SetForGameAsync(games[0], new[] { positiveTagId });
        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindRecurringConceptTag);
    }

    [Fact]
    public async Task GetPatternCardsAsync_RuleBreaks_GatedOnLiveSessionFlag()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var games = new long[3];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = await SeedRankedGameAsync(scope, 9161 + i, ageSeconds: (i + 1) * 3600);
            await scope.SessionLog.LogGameAsync(games[i], "Ahri", win: false, mentalRating: 5);
            await scope.SessionLog.SetRuleBrokenAsync(games[i], true);
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: games[i],
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.RuleBreakSourceKey,
                StartTimeSeconds: null,
                EndTimeSeconds: null,
                Title: PatternConstants.RuleBreakTitle,
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }

        var card = Assert.Single(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindRuleBreaks);
        Assert.Equal(3, card.MomentCount);
        Assert.Equal(card.MomentCount, (await scope.Evidence.GetPatternMomentsAsync(card)).Count);

        // Clearing one game's rule break (false positive) drops it from the
        // count via the live-signal EXISTS → below threshold, card gone.
        await scope.SessionLog.SetRuleBrokenAsync(games[0], false);
        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            c => c.Kind == PatternConstants.KindRuleBreaks);
    }

    [Fact]
    public async Task GetPatternCardsAsync_EveryCardsMomentCount_MatchesItsPlaylist()
    {
        // Anti-drift invariant: whatever detectors fire, the card's MomentCount
        // must equal the playlist GetPatternMomentsAsync resolves for it — a
        // count/playlist mismatch is how detection bugs hide.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Reset before dragon", "macro");
        var gameA = await SeedRankedGameAsync(scope, 9171, ageSeconds: 86_400);
        var gameB = await SeedRankedGameAsync(scope, 9172, ageSeconds: 2 * 86_400);

        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:100", 100, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "gank-death:300", 300, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "gank-death:200", 200, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpsertAsync(TitledMoment(gameA, "objective:dragon:900:960", 900, "Lost Dragon fight"));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "objective:baron:1500:1560", 1500, "Lost Baron fight"));
        await scope.Evidence.UpsertAsync(TitledMoment(gameB, "objective:dragon:700:760", 700, "Lost Dragon fight"));

        // A dismissed moment must leave BOTH the count and the playlist.
        var dismissed = await scope.Evidence.UpsertAsync(TitledMoment(gameB, "gank-death:500", 500, PatternConstants.GankDeathTitle));
        await scope.Evidence.UpdateStatusAsync(dismissed, EvidenceStatuses.Dismissed);

        // bad_objective_evidence with a mixed-polarity objective: the card must
        // count (and list) only the bad rows.
        foreach (var (key, polarity) in new[] { ("bo:1", EvidencePolarities.Bad), ("bo:2", EvidencePolarities.Bad), ("bo:3", EvidencePolarities.Good) })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameA,
                SourceKind: EvidenceKinds.Clip,
                SourceId: null,
                SourceKey: key,
                StartTimeSeconds: 50,
                EndTimeSeconds: 60,
                Title: "clip",
                ObjectiveId: objectiveId,
                Polarity: polarity,
                Status: EvidenceStatuses.Evidence));
        }

        var cards = await scope.Evidence.GetPatternCardsAsync();
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            var moments = await scope.Evidence.GetPatternMomentsAsync(card);
            Assert.Equal(card.MomentCount, moments.Count);
        }
    }
}
