using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The pattern-evidence materializer is the writer half of the patterns
/// contract: everything it produces must be exactly what the objective-driven
/// detectors in EvidenceRepository count (shared vocabulary in
/// PatternConstants). These tests pin the writer's output shape, its
/// idempotence, the clip-promotion re-key interplay, the retired-v1-row
/// cleanup, and the windowed backfill's stamp discipline.
/// </summary>
public sealed class PatternEvidenceMaterializerTests
{
    private static PatternEvidenceMaterializer Create(TestDatabaseScope scope) => new(
        scope.GameEvents,
        scope.Evidence,
        scope.Objectives,
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
    public async Task MaterializeForGame_AnchorsOnlyTheTokensActiveObjectivesTrack()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var objectiveId = await scope.Objectives.CreateAsync("Punish ganks with vision", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "JUNGLE_GANK", "DRAGON" });

        var gameId = await SeedRankedGameAsync(scope, 7401);
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            // A gank death matches JUNGLE_GANK (and DEATH — untracked).
            Ev(gameId, GameEvent.EventTypes.Death, 400, "{\"killer\":\"Nocturne\",\"jungle_gank\":true}"),
            // A plain death matches only the untracked DEATH token → no anchor.
            Ev(gameId, GameEvent.EventTypes.Death, 900),
            // A dragon take → tracked DRAGON anchor.
            Ev(gameId, GameEvent.EventTypes.Dragon, 1100),
            // A kill: untracked → nothing.
            Ev(gameId, GameEvent.EventTypes.Kill, 1500),
        });

        await materializer.MaterializeForGameAsync(gameId);

        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(2, rows.Count);

        var gank = Assert.Single(rows, r => r.SourceKey == PatternConstants.ObjEventSourceKey("JUNGLE_GANK", 400));
        Assert.Equal(PatternConstants.TokenLabel("JUNGLE_GANK"), gank.Title);
        Assert.Equal(EvidencePolarities.Bad, gank.Polarity);
        Assert.Equal(400 - PatternConstants.MomentLeadSeconds, gank.StartTimeSeconds);

        var dragon = Assert.Single(rows, r => r.SourceKey == PatternConstants.ObjEventSourceKey("DRAGON", 1100));
        Assert.Equal("Dragon", dragon.Title);
        Assert.Equal(EvidencePolarities.Neutral, dragon.Polarity);

        // Output contract: timeline_region + 'evidence' (never needs_review —
        // materialization must not flood the review queue's pending count).
        Assert.All(rows, r =>
        {
            Assert.Equal(EvidenceKinds.TimelineRegion, r.SourceKind);
            Assert.Equal(EvidenceStatuses.Evidence, r.Status);
        });
        Assert.Equal(0, await scope.Evidence.CountPendingAsync());
        Assert.Equal(PatternEvidenceMaterializer.Version, await ReadStampAsync(scope, gameId));

        // Idempotent (source_key dedupe).
        await materializer.MaterializeForGameAsync(gameId);
        Assert.Equal(2, (await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true)).Count);
    }

    [Fact]
    public async Task MaterializeForGame_TeamfightTracking_AnchorsOneRowPerCluster()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var objectiveId = await scope.Objectives.CreateAsync("Win teamfights", "teamfight");
        await scope.Objectives.SetEventTokensForObjectiveAsync(
            objectiveId, new[] { GameEvent.TrackableTokens.TeamfightToken });

        var gameId = await SeedRankedGameAsync(scope, 7402);
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            // Cluster: 4 combat events chained within 14s gaps → ONE anchor.
            Ev(gameId, GameEvent.EventTypes.Kill, 900),
            Ev(gameId, GameEvent.EventTypes.Death, 910),
            Ev(gameId, GameEvent.EventTypes.Assist, 920),
            Ev(gameId, GameEvent.EventTypes.Kill, 930),
            // A lone kill far away: no cluster, no anchor.
            Ev(gameId, GameEvent.EventTypes.Kill, 2000),
        });

        await materializer.MaterializeForGameAsync(gameId);

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(PatternConstants.ObjEventSourceKey(GameEvent.TrackableTokens.TeamfightToken, 900), row.SourceKey);
        Assert.Equal("Teamfight", row.Title);
        Assert.Equal(900 - PatternConstants.TeamfightLeadSeconds, row.StartTimeSeconds);
        Assert.Equal(930 + PatternConstants.TeamfightTrailSeconds, row.EndTimeSeconds);
    }

    [Fact]
    public async Task MaterializeForGame_NoActiveTrackedTokens_WritesNothingButStillStamps()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var gameId = await SeedRankedGameAsync(scope, 7403);
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            Ev(gameId, GameEvent.EventTypes.Death, 400, "{\"jungle_gank\":true}"),
        });

        await materializer.MaterializeForGameAsync(gameId);

        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(PatternEvidenceMaterializer.Version, await ReadStampAsync(scope, gameId));
    }

    [Fact]
    public async Task MaterializeForGame_DoesNotDuplicateAPromotedMoment()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var objectiveId = await scope.Objectives.CreateAsync("Track ganks", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "JUNGLE_GANK" });

        var gameId = await SeedRankedGameAsync(scope, 7404);
        await scope.GameEvents.SaveEventsAsync(gameId, new[]
        {
            Ev(gameId, GameEvent.EventTypes.Death, 400, "{\"jungle_gank\":true}"),
        });
        await materializer.MaterializeForGameAsync(gameId);
        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));

        // The note flow promotes it to a clip: source key rewritten, but the
        // token-label title and EXACT window survive (the note endpoint clips
        // a real start/end range verbatim).
        var bookmarkId = await scope.Vod.AddBookmarkAsync(
            gameId, row.StartTimeSeconds!.Value, "noted",
            clipStartSeconds: row.StartTimeSeconds, clipEndSeconds: row.EndTimeSeconds,
            clipPath: @"C:\clips\g.mp4");
        await scope.Evidence.AttachClipToEvidenceAsync(
            row.Id, bookmarkId, row.StartTimeSeconds!.Value, row.EndTimeSeconds!.Value);

        // Crash-recovery / version-bump path: re-materialization must not
        // re-insert the promoted moment under its original source key.
        await scope.Games.UpdatePatternEvidenceVersionAsync(gameId, 0);
        await materializer.MaterializeForGameAsync(gameId);

        var after = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(EvidenceKinds.Clip, after.SourceKind);
    }

    [Fact]
    public async Task ReviewSignals_AnchorFailedCriteriaOnly()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var failing = await scope.Objectives.CreateAsync("CS 7+/min by 10", "laning");
        var passing = await scope.Objectives.CreateAsync("Ward river by 3:00", "vision");
        var unevaluated = await scope.Objectives.CreateAsync("Free-text only", "mental");

        var gameId = await SeedRankedGameAsync(scope, 7405);
        foreach (var oid in new[] { failing, passing, unevaluated })
        {
            await scope.Objectives.RecordGameAsync(gameId, oid, practiced: true);
        }
        await scope.Objectives.SetCriteriaMetAsync(gameId, failing, met: false);
        await scope.Objectives.SetCriteriaMetAsync(gameId, passing, met: true);

        await materializer.MaterializeReviewSignalsAsync(gameId);

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
        Assert.Equal(PatternConstants.ObjCritSourceKey(failing), row.SourceKey);
        Assert.Equal(PatternConstants.ObjCritTitle("CS 7+/min by 10"), row.Title);
        Assert.Null(row.StartTimeSeconds); // game-level anchor: no in-game second

        // Idempotent, like everything keyed on source_key.
        await materializer.MaterializeReviewSignalsAsync(gameId);
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
    }

    [Fact]
    public async Task MaterializeForGame_CleansUpRetiredV1Rows_PreservingNotedOnes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var gameId = await SeedRankedGameAsync(scope, 7406);

        // Retired v3.5 materialized rows of every family.
        foreach (var (key, title) in new[]
        {
            ("gank-death:400", "Death to gank"),
            ("death-audit:380", "Death: GREED"),
            ("tag:12", "Caught out"),
            ("rulebreak", "Broke a queue rule"),
            ("objective:dragon:886:911", "Lost Dragon fight"),
            ("objective-death:baron:1457:1503", "Death before Baron"),
        })
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
                SourceKey: key, StartTimeSeconds: 100, EndTimeSeconds: 110, Title: title,
                Polarity: EvidencePolarities.Bad, Status: EvidenceStatuses.Evidence));
        }
        // One retired row carrying user writing — must survive.
        var noted = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "gank-death:700", StartTimeSeconds: 694, EndTimeSeconds: 708,
            Title: "Death to gank", Polarity: EvidencePolarities.Bad, Status: EvidenceStatuses.Evidence));
        await scope.Evidence.UpdateNoteAsync(noted, "I keep face-checking here");
        // A non-materialized user row (manual clip) — untouched.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId, SourceKind: EvidenceKinds.Clip, SourceId: 5, SourceKey: "clip:5",
            StartTimeSeconds: 200, EndTimeSeconds: 220, Title: "My clip",
            Status: EvidenceStatuses.Evidence));

        await materializer.MaterializeForGameAsync(gameId);

        var rows = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == noted && r.Note == "I keep face-checking here");
        Assert.Contains(rows, r => r.SourceKey == "clip:5");
    }

    [Fact]
    public async Task BackfillWindow_ProcessesOnlyUnstampedWindowGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var materializer = Create(scope);

        var objectiveId = await scope.Objectives.CreateAsync("Track ganks", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objectiveId, new[] { "JUNGLE_GANK" });

        var fresh = await SeedRankedGameAsync(scope, 7411, ageSeconds: 3600);
        var alsoFresh = await SeedRankedGameAsync(scope, 7412, ageSeconds: 7200);
        var preStamped = await SeedRankedGameAsync(scope, 7413, ageSeconds: 10_800);
        var stale = await SeedRankedGameAsync(scope, 7414, ageSeconds: (PatternConstants.WindowDays + 3) * 86_400L);

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
        Assert.Empty(await scope.Evidence.GetForGameAsync(preStamped, includeDismissed: true));
        Assert.Empty(await scope.Evidence.GetForGameAsync(stale, includeDismissed: true));
        Assert.Null(await ReadStampAsync(scope, stale));

        // A second pass finds nothing left to do.
        Assert.Equal(0, await materializer.BackfillWindowAsync());
    }
}
