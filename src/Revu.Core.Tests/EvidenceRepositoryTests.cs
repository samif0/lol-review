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
    // Cross-pattern selection uses only explicitly saved objective moments.

    private static async Task<long> SeedRankedGameAsync(
        TestDatabaseScope scope, long gameId, long ageSeconds = 3600)
    {
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ageSeconds));
        return gameId;
    }

    private static Task<long> SaveClipAsync(TestDatabaseScope scope, long gameId,
        long objectiveId, int timeS, string polarity = EvidencePolarities.Neutral) =>
        scope.Vod.AddBookmarkAsync(gameId, timeS, "reviewed moment",
            clipStartSeconds: timeS, clipEndSeconds: timeS + 20,
            clipPath: $@"C:\clips\{gameId}-{timeS}.mp4", objectiveId: objectiveId, quality: polarity);

    private static ObjectivePatternCard SavedCard(long objectiveId) => new(
        PatternConstants.KindSavedObjectiveEvidence, "", "", ObjectiveId: objectiveId);

    private static Task<long> SaveLinkedEvidenceAsync(TestDatabaseScope scope, long gameId,
        long bookmarkId, long objectiveId, string key, string polarity = EvidencePolarities.Neutral) =>
        scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId, SourceKind: EvidenceKinds.Clip, SourceId: bookmarkId,
            SourceKey: key, StartTimeSeconds: 100, EndTimeSeconds: 120,
            Title: "Saved clip", ObjectiveId: objectiveId, Polarity: polarity,
            Status: EvidenceStatuses.Evidence));

    [Fact]
    public async Task CrossPatterns_IgnoreAbundantRawEventsAndFailedCriteria_EvenWhenTaggedOrNoted()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Find good fights", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "KILL", "DEATH" });

        for (var gameIndex = 0; gameIndex < 3; gameIndex++)
        {
            var gameId = await SeedRankedGameAsync(scope, 9200 + gameIndex);
            await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true);
            await scope.Objectives.SetCriteriaMetAsync(gameId, objectiveId, met: false);
            for (var i = 0; i < 18; i++)
            {
                await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                    GameId: gameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
                    SourceKey: PatternConstants.ObjEventSourceKey("KILL", i * 60),
                    StartTimeSeconds: i * 60, EndTimeSeconds: i * 60 + 14, Title: "Kill",
                    Note: "Tagged event is still not a saved clip or bookmark",
                    ObjectiveId: objectiveId, Polarity: EvidencePolarities.Bad,
                    Status: EvidenceStatuses.Evidence));
            }
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
                SourceKey: PatternConstants.ObjCritSourceKey(objectiveId),
                StartTimeSeconds: null, EndTimeSeconds: null, Title: "Missed criterion",
                ObjectiveId: objectiveId, Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }

        Assert.Empty(await scope.Evidence.GetPatternCardsAsync());
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(SavedCard(objectiveId)));
        foreach (var legacyKind in new[] { PatternConstants.KindObjectiveEvents, PatternConstants.KindObjectiveCriteria })
        {
            Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(new ObjectivePatternCard(
                legacyKind, "", "", ObjectiveId: objectiveId, Discriminator: "KILL")));
        }
    }

    [Fact]
    public async Task SavedCollection_IncludesTaggedClipsAndBookmarks_WithoutRequiringEvidenceOrBadRating()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var gameId = await SeedRankedGameAsync(scope, 9210);
        await scope.Vod.LinkVodAsync(gameId, @"C:\vods\tempo.mp4");
        var bookmarkId = await scope.Vod.AddBookmarkAsync(gameId, 150, "missed reset", objectiveId: objectiveId);
        var clipId = await SaveClipAsync(scope, gameId, objectiveId, 300, EvidencePolarities.Good);
        await scope.Vod.AddBookmarkAsync(gameId, 600, "untagged");

        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(PatternConstants.KindSavedObjectiveEvidence, card.Kind);
        Assert.Equal(2, card.MomentCount);
        Assert.Equal(1, card.GameCount);
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(2, moments.Count);
        Assert.Equal(bookmarkId, moments[0].BookmarkId);
        Assert.Equal(0, moments[0].EvidenceId);
        Assert.Equal("bookmark", moments[0].SourceKind);
        Assert.Equal(150, moments[0].StartTimeSeconds);
        Assert.Equal(150, moments[0].EndTimeSeconds);
        Assert.Equal("missed reset", moments[0].Note);
        Assert.Equal(@"C:\vods\tempo.mp4", moments[0].VodPath);
        Assert.Equal(clipId, moments[1].BookmarkId);
        Assert.Equal(EvidenceKinds.Clip, moments[1].SourceKind);
        Assert.Equal(300, moments[1].StartTimeSeconds);
        Assert.Equal(320, moments[1].EndTimeSeconds);
        Assert.Equal(EvidencePolarities.Good, moments[1].Polarity);
        Assert.All(moments, moment => Assert.True(moment.CreatedAt > 0));
        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(3, (await scope.Vod.GetBookmarksAsync(gameId)).Count);
    }

    [Fact]
    public async Task SavedCollection_RequiresAnExplicitTagToACurrentActiveObjective()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var active = await scope.Objectives.CreateAsync("Tempo", "macro");
        var archived = await scope.Objectives.CreateAsync("Old focus", "macro");
        var gameId = await SeedRankedGameAsync(scope, 9220);
        await scope.Objectives.RecordGameAsync(gameId, active, practiced: true);
        await scope.Objectives.SetEventTokensForObjectiveAsync(active, new[] { "KILL" });
        await scope.Vod.AddBookmarkAsync(gameId, 100, "Kill");
        await SaveClipAsync(scope, gameId, archived, 200);
        await SaveClipAsync(scope, gameId, active, 300);
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE objectives SET status = 'archived' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", archived);
            await cmd.ExecuteNonQueryAsync();
        }

        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(active, card.ObjectiveId);
        Assert.Equal(1, card.MomentCount);
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(SavedCard(archived)));
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE objectives SET status = 'archived' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", active);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Empty(await scope.Evidence.GetPatternCardsAsync());
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(card));
    }

    [Fact]
    public async Task SavedCollection_DeduplicatesBookmarkAndEvidenceRows_AndHonorsDismissal()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var gameId = await SeedRankedGameAsync(scope, 9230);
        var bookmarkId = await SaveClipAsync(scope, gameId, objectiveId, 100);
        await SaveLinkedEvidenceAsync(scope, gameId, bookmarkId, objectiveId, "old-clip-row");
        var currentEvidence = await SaveLinkedEvidenceAsync(scope, gameId, bookmarkId, objectiveId, "clip:" + bookmarkId);

        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(1, card.MomentCount);
        var moment = Assert.Single(await scope.Evidence.GetPatternMomentsAsync(card));
        Assert.Equal(bookmarkId, moment.BookmarkId);
        Assert.Equal(currentEvidence, moment.EvidenceId);
        await scope.Evidence.UpdateStatusAsync(currentEvidence, EvidenceStatuses.Dismissed);
        Assert.Empty(await scope.Evidence.GetPatternCardsAsync());
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(card));
    }

    [Fact]
    public async Task BadTrends_RequireMistakesAcrossDistinctGames_AndDoNotRequireBadToOutnumberGood()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var gameA = await SeedRankedGameAsync(scope, 9240);
        var gameB = await SeedRankedGameAsync(scope, 9241);
        for (var i = 0; i < 5; i++)
            await SaveClipAsync(scope, gameA, objectiveId, 100 + i * 30, EvidencePolarities.Bad);
        Assert.DoesNotContain(await scope.Evidence.GetPatternCardsAsync(),
            card => card.Kind == PatternConstants.KindBadObjectiveEvidence);

        await scope.Vod.AddBookmarkAsync(gameB, 200, "same mistake", objectiveId: objectiveId, quality: EvidencePolarities.Bad);
        for (var i = 0; i < 8; i++)
            await SaveClipAsync(scope, gameB, objectiveId, 300 + i * 30, EvidencePolarities.Good);
        var cards = await scope.Evidence.GetPatternCardsAsync();
        var trend = Assert.Single(cards, card => card.Kind == PatternConstants.KindBadObjectiveEvidence);
        Assert.Equal(6, trend.MomentCount);
        Assert.Equal(2, trend.GameCount);
        Assert.All(await scope.Evidence.GetPatternMomentsAsync(trend), moment => Assert.Equal(EvidencePolarities.Bad, moment.Polarity));
        var saved = Assert.Single(cards, card => card.Kind == PatternConstants.KindSavedObjectiveEvidence);
        Assert.Equal(14, saved.MomentCount);
        foreach (var card in cards)
        {
            var moments = await scope.Evidence.GetPatternMomentsAsync(card);
            Assert.Equal(card.MomentCount, moments.Count);
            Assert.Equal(card.GameCount, moments.Select(moment => moment.GameId).Distinct().Count());
        }
    }

    [Fact]
    public async Task SavedCollectionsKeepHistory_WhileMistakeTrendsUseRecentGamesOnly()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var recentA = await SeedRankedGameAsync(scope, 9250);
        var recentB = await SeedRankedGameAsync(scope, 9251);
        var old = await SeedRankedGameAsync(scope, 9252, (PatternConstants.WindowDays + 5) * 86400L);
        await SaveClipAsync(scope, recentA, objectiveId, 100, EvidencePolarities.Bad);
        await SaveClipAsync(scope, old, objectiveId, 100, EvidencePolarities.Bad);
        var saved = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(PatternConstants.KindSavedObjectiveEvidence, saved.Kind);
        Assert.Equal(2, saved.MomentCount);
        await SaveClipAsync(scope, recentB, objectiveId, 100, EvidencePolarities.Bad);
        var cards = await scope.Evidence.GetPatternCardsAsync();
        var trend = Assert.Single(cards, card => card.Kind == PatternConstants.KindBadObjectiveEvidence);
        Assert.Equal(2, trend.MomentCount);
        Assert.DoesNotContain(await scope.Evidence.GetPatternMomentsAsync(trend), moment => moment.GameId == old);
        Assert.Equal(3, Assert.Single(cards, card => card.Kind == PatternConstants.KindSavedObjectiveEvidence).MomentCount);
    }

    [Fact]
    public async Task SavedCollections_KeepReviewedAndSkippedGames_AndLegacyClipEvidence()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Stay in line with support", "laning");
        var reviewed = await SeedRankedGameAsync(scope, 9260);
        var skipped = await SeedRankedGameAsync(scope, 9261);
        foreach (var gameId in new[] { reviewed, skipped })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId, SourceKind: EvidenceKinds.Clip, SourceId: gameId,
                SourceKey: "legacy-clip:" + gameId, StartTimeSeconds: 100, EndTimeSeconds: 120,
                Title: "Spacing", ObjectiveId: objectiveId, Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }
        await scope.Games.UpdateReviewAsync(reviewed, new GameReview { Rating = 4, Notes = "Reviewed" });
        await scope.SessionLog.LogGameAsync(skipped, "Ahri", win: false, mentalRating: 5);
        await scope.SessionLog.MarkSkippedAsync(skipped);
        var cards = await scope.Evidence.GetPatternCardsAsync();
        Assert.Equal(2, cards.Count);
        foreach (var card in cards)
        {
            Assert.Equal(2, card.MomentCount);
            var moments = await scope.Evidence.GetPatternMomentsAsync(card);
            Assert.Equal(2, moments.Count);
            Assert.All(moments, moment => Assert.Null(moment.BookmarkId));
        }
    }

    [Fact]
    public async Task SavedCollections_IncludeNormalGamesButExcludeHiddenGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var normal = await SeedRankedGameAsync(scope, 9270);
        var hidden = await SeedRankedGameAsync(scope, 9271);
        await SaveClipAsync(scope, normal, objectiveId, 100);
        await SaveClipAsync(scope, hidden, objectiveId, 100);
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE games SET queue_type = 'Normal Draft' WHERE game_id = @normal;
                UPDATE games SET is_hidden = 1 WHERE game_id = @hidden;
                """;
            cmd.Parameters.AddWithValue("@normal", normal);
            cmd.Parameters.AddWithValue("@hidden", hidden);
            await cmd.ExecuteNonQueryAsync();
        }

        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(1, card.MomentCount);
        Assert.Equal(normal, Assert.Single(await scope.Evidence.GetPatternMomentsAsync(card)).GameId);
    }

    [Fact]
    public async Task SavedCollection_LegacyEvidenceTagWorksAndDuplicateSourceIdsCountOnce()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Tempo", "macro");
        var gameId = await SeedRankedGameAsync(scope, 9280);
        var bookmarkId = await scope.Vod.AddBookmarkAsync(gameId, 50, "bookmark tagged through evidence");
        await SaveLinkedEvidenceAsync(scope, gameId, bookmarkId, objectiveId, "tagged-bookmark");
        await SaveLinkedEvidenceAsync(scope, gameId, 999, objectiveId, "legacy-first");
        var latest = await SaveLinkedEvidenceAsync(scope, gameId, 999, objectiveId, "legacy-second");

        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(2, card.MomentCount);
        var moments = await scope.Evidence.GetPatternMomentsAsync(card);
        Assert.Equal(bookmarkId, moments[0].BookmarkId);
        Assert.Equal("bookmark", moments[0].SourceKind);
        Assert.Equal(50, moments[0].StartTimeSeconds);
        Assert.Equal(latest, moments[1].EvidenceId);
        Assert.Null(moments[1].BookmarkId);
    }

    [Fact]
    public async Task LinkedClip_UsesLiveEvidenceTagAndNote_IncludingExplicitClears()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var originalObjective = await scope.Objectives.CreateAsync("Tempo", "macro");
        var newObjective = await scope.Objectives.CreateAsync("Vision", "macro");
        var gameId = await SeedRankedGameAsync(scope, 9290);
        var bookmarkId = await SaveClipAsync(scope, gameId, originalObjective, 100);
        var evidenceId = await SaveLinkedEvidenceAsync(scope, gameId, bookmarkId, originalObjective, "clip:" + bookmarkId);

        await scope.Evidence.UpdateObjectiveAsync(evidenceId, newObjective);
        await scope.Evidence.UpdateNoteAsync(evidenceId, "Reconsidered this decision");
        var card = Assert.Single(await scope.Evidence.GetPatternCardsAsync());
        Assert.Equal(newObjective, card.ObjectiveId);
        Assert.Equal("Reconsidered this decision", Assert.Single(await scope.Evidence.GetPatternMomentsAsync(card)).Note);
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(SavedCard(originalObjective)));

        await scope.Evidence.UpdateNoteAsync(evidenceId, "");
        Assert.Equal("", Assert.Single(await scope.Evidence.GetPatternMomentsAsync(card)).Note);
        await scope.Evidence.UpdateObjectiveAsync(evidenceId, null);
        Assert.Empty(await scope.Evidence.GetPatternCardsAsync());
        Assert.Empty(await scope.Evidence.GetPatternMomentsAsync(card));
    }
}
