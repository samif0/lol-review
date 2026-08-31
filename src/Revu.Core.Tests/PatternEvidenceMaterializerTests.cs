using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The pattern-evidence materializer is the writer half of the patterns
/// contract: everything it produces must be exactly what the detectors in
/// EvidenceRepository count (shared vocabulary in PatternConstants). These tests
/// pin the writer's output shape, its idempotence, the clip-promotion re-key
/// interplay, and the windowed backfill's stamp discipline.
/// </summary>
public sealed class PatternEvidenceMaterializerTests
{
    private static PatternEvidenceMaterializer Create(TestDatabaseScope scope, DeathClassificationsRepository deathClassifications) => new(
        scope.GameEvents,
        scope.Evidence,
        deathClassifications,
        scope.ConceptTags,
        scope.SessionLog,
        scope.Games,
        NullLogger<PatternEvidenceMaterializer>.Instance);

    private static async Task<long> SeedRankedGameAsync(TestDatabaseScope scope, long gameId, long ageSeconds = 3600)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, timestamp: now - ageSeconds));
        return gameId;
    }

    private static GameEvent Ev(long gameId, string type, int timeS, string details = "{}") =>
        new() { GameId = gameId, EventType = type, GameTimeS = timeS, Details = details };

    private static async Task<long?> ReadStampAsync(TestDatabaseScope scope, long gameId)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pattern_evidence_v FROM games WHERE game_id = @id";
        cmd.Parameters.AddWithValue("@id", gameId);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    [Fact]
    public async Task MaterializeForGame_ProducesExactlyThePatternRelevantRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var deathClassifications = new DeathClassificationsRepository(scope.ConnectionFactory);
        var materializer = Create(scope, deathClassifications);

        var gameId = await SeedRankedGameAsync(scope, 7301);
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            // A gank death (Details.jungle_gank stamped at capture).
            Ev(gameId, GameEvent.EventTypes.Death, 400, "{\"killer\":\"Nocturne\",\"jungle_gank\":true}"),
            // A LOST dragon fight: 2 deaths, 0 kills around the DRAGON event.
            Ev(gameId, GameEvent.EventTypes.Death, 890),
            Ev(gameId, GameEvent.EventTypes.Dragon, 900),
            Ev(gameId, GameEvent.EventTypes.Death, 905),
            // A death 40s before BARON → "Death before Baron".
            Ev(gameId, GameEvent.EventTypes.Death, 1460),
            Ev(gameId, GameEvent.EventTypes.Baron, 1500),
            // Malformed details must be skipped, never thrown on.
            Ev(gameId, GameEvent.EventTypes.Death, 2000, "not-json{{"),
        });

        await materializer.MaterializeForGameAsync(gameId);

        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Title == "Lost Dragon fight");
        Assert.Contains(rows, r => r.Title == "Death before Baron");
        Assert.Contains(rows, r => r.Title == PatternConstants.GankDeathTitle
            && r.SourceKey == PatternConstants.GankDeathSourceKey(400));

        // Output contract: timeline_region, bad, 'evidence' (never needs_review
        // — materialization must not flood the review queue's pending count).
        Assert.All(rows, r =>
        {
            Assert.Equal(EvidenceKinds.TimelineRegion, r.SourceKind);
            Assert.Equal(EvidencePolarities.Bad, r.Polarity);
            Assert.Equal(EvidenceStatuses.Evidence, r.Status);
        });
        Assert.Equal(0, await scope.Evidence.CountPendingAsync());

        // The game is stamped at the current materializer version.
        Assert.Equal(PatternEvidenceMaterializer.Version, await ReadStampAsync(scope, gameId));

        // Idempotence: a second run adds nothing (source_key dedupe).
        await materializer.MaterializeForGameAsync(gameId);
        Assert.Equal(3, (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Count);
    }

    [Fact]
    public async Task MaterializeForGame_DoesNotMaterializeWonFightsOrLoneDeaths()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope, new DeathClassificationsRepository(scope.ConnectionFactory));

        var gameId = await SeedRankedGameAsync(scope, 7302);
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            // A WON dragon fight: 3 positives, 0 deaths.
            Ev(gameId, GameEvent.EventTypes.Kill, 890),
            Ev(gameId, GameEvent.EventTypes.Dragon, 900),
            Ev(gameId, GameEvent.EventTypes.Kill, 895),
            Ev(gameId, GameEvent.EventTypes.Assist, 905),
            // A lone, unclassified, non-gank death far from any objective.
            Ev(gameId, GameEvent.EventTypes.Death, 2000),
        });

        await materializer.MaterializeForGameAsync(gameId);

        // Nothing pattern-relevant: no rows at all (lone deaths were the retired
        // isolated_deaths kind — isolation is unprovable from the kill feed).
        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(PatternEvidenceMaterializer.Version, await ReadStampAsync(scope, gameId));
    }

    [Fact]
    public async Task ClassifiedDeath_UpsertRetitleClipPromotionAndClear_RoundTrip()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var deathClassifications = new DeathClassificationsRepository(scope.ConnectionFactory);
        var materializer = Create(scope, deathClassifications);

        var gameId = await SeedRankedGameAsync(scope, 7303);

        // Classify → one audit row keyed on the death second.
        await deathClassifications.UpsertAsync(gameId, 380, DeathClasses.Greed);
        await materializer.UpsertClassifiedDeathAsync(gameId, 380, DeathClasses.Greed);

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(PatternConstants.DeathAuditTitle(DeathClasses.LabelFor(DeathClasses.Greed)), row.Title);
        Assert.Equal(PatternConstants.DeathAuditSourceKey(380), row.SourceKey);
        Assert.Equal(380 - PatternConstants.DeathMomentLeadSeconds, row.StartTimeSeconds);
        Assert.Equal(380 + PatternConstants.DeathMomentTrailSeconds, row.EndTimeSeconds);

        // Re-classify → SAME row, new title.
        await materializer.UpsertClassifiedDeathAsync(gameId, 380, DeathClasses.Vision);
        var retitled = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(row.Id, retitled.Id);
        Assert.Equal("Death: VISION", retitled.Title);

        // Promote to a clip (the note flow's silent clip-keep): source_key is
        // rewritten to clip:{bookmarkId} but the title survives.
        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId, 374, "noted", clipStartSeconds: 374, clipEndSeconds: 388, clipPath: @"C:\clips\x.mp4");
        await scope.Evidence.AttachClipToEvidenceAsync(row.Id, bookmarkId, 374, 388);

        // THE REKEY PIN — after promotion:
        // (a) re-running the full materializer inserts no duplicate;
        await materializer.MaterializeForGameAsync(gameId);
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));

        // (b) re-classifying retitles the PROMOTED row instead of inserting a twin;
        await materializer.UpsertClassifiedDeathAsync(gameId, 380, DeathClasses.Tempo);
        var promoted = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(row.Id, promoted.Id);
        Assert.Equal("Death: TEMPO", promoted.Title);
        Assert.Equal(EvidenceKinds.Clip, promoted.SourceKind);

        // (c) clearing keeps the user's clip row but retitles it out of the mix.
        await materializer.ClearClassifiedDeathAsync(gameId, 380);
        var cleared = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(row.Id, cleared.Id);
        Assert.Equal(PatternConstants.ClearedDeathAuditTitle, cleared.Title);

        // An UN-promoted audit row is deleted outright on clear.
        await materializer.UpsertClassifiedDeathAsync(gameId, 500, DeathClasses.Wave);
        Assert.Equal(2, (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Count);
        await materializer.ClearClassifiedDeathAsync(gameId, 500);
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));

        // Unknown class keys write nothing.
        await materializer.UpsertClassifiedDeathAsync(gameId, 600, "definitely-not-a-class");
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
    }

    [Fact]
    public async Task ReviewSignals_AnchorNegativeTagsAndRuleBreaksOnly()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope, new DeathClassificationsRepository(scope.ConnectionFactory));

        var gameId = await SeedRankedGameAsync(scope, 7304);
        var negativeTagId = await scope.ConceptTags.CreateAsync("Caught out again", "negative", "#ef4444");
        var positiveTagId = await scope.ConceptTags.CreateAsync("Won lane hard", "positive", "#22c55e");
        await scope.ConceptTags.SetForGameAsync(gameId, new[] { negativeTagId, positiveTagId });
        await scope.SessionLog.LogGameAsync(gameId, "Ahri", win: false, mentalRating: 5);
        await scope.SessionLog.SetRuleBrokenAsync(gameId, true);

        await materializer.MaterializeReviewSignalsAsync(gameId);

        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(2, rows.Count);

        var tagAnchor = Assert.Single(rows, r => r.SourceKey == PatternConstants.TagSourceKey(negativeTagId));
        Assert.Equal("Caught out again", tagAnchor.Title);
        Assert.Null(tagAnchor.StartTimeSeconds); // game-level anchor: no in-game second

        Assert.Single(rows, r => r.SourceKey == PatternConstants.RuleBreakSourceKey
            && r.Title == PatternConstants.RuleBreakTitle);

        // The positive tag anchors nothing.
        Assert.DoesNotContain(rows, r => r.SourceKey == PatternConstants.TagSourceKey(positiveTagId));

        // Idempotent, like everything keyed on source_key.
        await materializer.MaterializeReviewSignalsAsync(gameId);
        Assert.Equal(2, (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Count);
    }

    [Fact]
    public async Task BackfillWindow_ProcessesOnlyUnstampedWindowGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope, new DeathClassificationsRepository(scope.ConnectionFactory));

        var fresh = await SeedRankedGameAsync(scope, 7311, ageSeconds: 3600);
        var alsoFresh = await SeedRankedGameAsync(scope, 7312, ageSeconds: 7200);
        var preStamped = await SeedRankedGameAsync(scope, 7313, ageSeconds: 10_800);
        var stale = await SeedRankedGameAsync(scope, 7314, ageSeconds: (PatternConstants.WindowDays + 3) * 86_400L);

        foreach (var id in new[] { fresh, alsoFresh, preStamped, stale })
        {
            await scope.GameEvents.SaveEventsAsync(id, new[]
            {
                Ev(id, GameEvent.EventTypes.Death, 400, "{\"jungle_gank\":true}"),
            });
        }
        await scope.Games.UpdatePatternEvidenceVersionAsync(preStamped, PatternEvidenceMaterializer.Version);

        var processed = await materializer.BackfillWindowAsync();

        Assert.Equal(2, processed);
        Assert.Single(await scope.Evidence.GetForGameAsync(fresh, includeDismissed: true));
        Assert.Single(await scope.Evidence.GetForGameAsync(alsoFresh, includeDismissed: true));
        // Pre-stamped and out-of-window games are untouched.
        Assert.Empty(await scope.Evidence.GetForGameAsync(preStamped, includeDismissed: true));
        Assert.Empty(await scope.Evidence.GetForGameAsync(stale, includeDismissed: true));
        Assert.Null(await ReadStampAsync(scope, stale));

        // A second pass finds nothing left to do.
        Assert.Equal(0, await materializer.BackfillWindowAsync());
    }
}
