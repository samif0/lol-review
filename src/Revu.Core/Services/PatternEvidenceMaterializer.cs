#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Produces the evidence rows the pattern detectors count. This is the bridge
/// the WinUI→Tauri migration severed: the old VOD player ran
/// <see cref="TimelineInferenceService"/> and upserted its regions as evidence;
/// when that ViewModel was deleted nothing wrote pattern-qualifying rows again
/// and the Patterns page went permanently empty. The materializer restores the
/// bridge — automatically at game end, incrementally from the death-classify
/// and review-save endpoints, and via a windowed startup backfill for games
/// already in the DB.
/// </summary>
public interface IPatternEvidenceMaterializer
{
    /// <summary>Materialize one game's pattern evidence (regions, gank deaths,
    /// classified deaths, review signals) and stamp games.pattern_evidence_v.</summary>
    Task MaterializeForGameAsync(long gameId);

    /// <summary>Upsert the death-audit moment for one classified death (called
    /// from POST /api/death/classify after the classification row is written).
    /// Re-classifying retitles in place — including a moment the note flow
    /// already promoted to a clip. Unknown class keys are ignored.</summary>
    Task UpsertClassifiedDeathAsync(long gameId, int gameTimeSeconds, string deathClassKey);

    /// <summary>Remove the death-audit moment for a cleared classification. An
    /// un-promoted row is deleted; a promoted row (the user kept a clip + note)
    /// is retitled to the neutral cleared title so it leaves every count but the
    /// clip survives.</summary>
    Task ClearClassifiedDeathAsync(long gameId, int gameTimeSeconds);

    /// <summary>Materialize the game-level review-signal anchors (negative
    /// concept tags, rule break) — called after a review save, and as part of
    /// <see cref="MaterializeForGameAsync"/>.</summary>
    Task MaterializeReviewSignalsAsync(long gameId);

    /// <summary>Materialize every window game not yet stamped at
    /// <see cref="PatternEvidenceMaterializer.Version"/>. Per-game failures are
    /// logged and skipped so one bad game never aborts the pass. Returns the
    /// number of games processed.</summary>
    Task<int> BackfillWindowAsync();
}

public sealed class PatternEvidenceMaterializer : IPatternEvidenceMaterializer
{
    /// <summary>Bump to re-queue every window game for the backfill (the exact
    /// MapStateAnalyzer.Version contract).</summary>
    public const int Version = 1;

    private readonly IGameEventsRepository _gameEvents;
    private readonly IEvidenceRepository _evidence;
    private readonly IDeathClassificationsRepository _deathClassifications;
    private readonly IConceptTagRepository _conceptTags;
    private readonly ISessionLogRepository _sessionLog;
    private readonly IGameRepository _games;
    private readonly ILogger<PatternEvidenceMaterializer> _logger;

    public PatternEvidenceMaterializer(
        IGameEventsRepository gameEvents,
        IEvidenceRepository evidence,
        IDeathClassificationsRepository deathClassifications,
        IConceptTagRepository conceptTags,
        ISessionLogRepository sessionLog,
        IGameRepository games,
        ILogger<PatternEvidenceMaterializer> logger)
    {
        _gameEvents = gameEvents;
        _evidence = evidence;
        _deathClassifications = deathClassifications;
        _conceptTags = conceptTags;
        _sessionLog = sessionLog;
        _games = games;
        _logger = logger;
    }

    public async Task MaterializeForGameAsync(long gameId)
    {
        var events = await _gameEvents.GetEventsAsync(gameId);

        // (a) Pattern-relevant inferred regions, selected by structured Kind —
        // never by parsing display names (name-parsing is how the old reader and
        // writer drifted apart). The promoted-twin probe keeps re-materialization
        // (crash-recovery backfill, a future Version bump) from re-inserting a
        // moment the note flow promoted — the promotion rekeys the row out from
        // under its source key, so source-key dedupe alone can't see it.
        foreach (var region in TimelineInferenceService.Infer(events))
        {
            if (region.Kind is not (PatternRegionKinds.LostObjectiveFight
                or PatternRegionKinds.DeathBeforeObjective))
            {
                continue;
            }
            if (await _evidence.FindPromotedTwinAsync(
                    gameId, region.Name, region.StartTimeSeconds, region.EndTimeSeconds) is not null)
            {
                continue;
            }
            await _evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: region.SourceKey,
                StartTimeSeconds: region.StartTimeSeconds,
                EndTimeSeconds: region.EndTimeSeconds,
                Title: region.Name,
                Polarity: EvidencePolarities.Bad,
                // 'evidence', not 'needs_review': materialized rows must never
                // flood the review queue's pending count.
                Status: EvidenceStatuses.Evidence));
        }

        // (b) Jungle-gank deaths (Details.jungle_gank stamped at capture by
        // JungleGankClassifier — fully automatic, zero user input). Same
        // promoted-twin guard as (a).
        foreach (var e in events)
        {
            if (!string.Equals(e.EventType, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase)
                || !ReadDetailsBool(e.Details, "jungle_gank"))
            {
                continue;
            }
            var startS = Math.Max(0, e.GameTimeS - PatternConstants.DeathMomentLeadSeconds);
            var endS = e.GameTimeS + PatternConstants.DeathMomentTrailSeconds;
            if (await _evidence.FindPromotedTwinAsync(gameId, PatternConstants.GankDeathTitle, startS, endS) is not null)
            {
                continue;
            }
            await _evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.GankDeathSourceKey(e.GameTimeS),
                StartTimeSeconds: startS,
                EndTimeSeconds: endS,
                Title: PatternConstants.GankDeathTitle,
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }

        // (c) Already-classified deaths (the backfill path; live classifications
        // arrive through UpsertClassifiedDeathAsync from the endpoint hook) —
        // plus reconciliation: an un-promoted audit row whose classification is
        // gone (e.g. a clear whose mirror write failed) is removed, so the
        // backfill heals the ledger in both directions.
        var classifications = await _deathClassifications.GetForGameAsync(gameId);
        foreach (var dc in classifications)
        {
            await UpsertClassifiedDeathAsync(gameId, dc.GameTimeSeconds, dc.DeathClass);
        }
        var classifiedSeconds = classifications.Select(static dc => dc.GameTimeSeconds).ToHashSet();
        foreach (var row in await _evidence.GetForGameAsync(gameId, includeDismissed: true))
        {
            if (row.SourceKind == EvidenceKinds.TimelineRegion
                && row.SourceKey.StartsWith(PatternConstants.DeathAuditSourceKeyPrefix, StringComparison.Ordinal)
                && int.TryParse(row.SourceKey[PatternConstants.DeathAuditSourceKeyPrefix.Length..], out var t)
                && !classifiedSeconds.Contains(t))
            {
                await ClearClassifiedDeathAsync(gameId, t);
            }
        }

        // (d) Game-level review signals (negative tags, rule break).
        await MaterializeReviewSignalsAsync(gameId);

        await _games.UpdatePatternEvidenceVersionAsync(gameId, Version);
    }

    public async Task UpsertClassifiedDeathAsync(long gameId, int gameTimeSeconds, string deathClassKey)
    {
        var label = DeathClasses.LabelFor(deathClassKey);
        if (label.Length == 0)
        {
            return; // unknown/empty class — don't invent a title no detector matches
        }
        var title = PatternConstants.DeathAuditTitle(label);

        // A moment the note flow promoted to a clip was rekeyed (source_key ->
        // clip:{bookmarkId}), so the source-key upsert below would insert a twin.
        // Retitle the promoted row in place instead — the user's clip and note
        // stay attached to the (re)classified death.
        if (await _evidence.FindPromotedDeathAuditAsync(gameId, gameTimeSeconds) is long promotedId)
        {
            await _evidence.UpdateTitleAsync(promotedId, title);
            return;
        }

        await _evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: PatternConstants.DeathAuditSourceKey(gameTimeSeconds),
            StartTimeSeconds: Math.Max(0, gameTimeSeconds - PatternConstants.DeathMomentLeadSeconds),
            EndTimeSeconds: gameTimeSeconds + PatternConstants.DeathMomentTrailSeconds,
            Title: title,
            Polarity: EvidencePolarities.Bad,
            Status: EvidenceStatuses.Evidence));
    }

    public async Task ClearClassifiedDeathAsync(long gameId, int gameTimeSeconds)
    {
        // A user note can land on an UN-promoted audit row too (the note flow
        // saves notes even when there's no VOD to clip) — a noted row is
        // retitled out of the counts instead of deleted, same as a promoted one.
        var sourceKey = PatternConstants.DeathAuditSourceKey(gameTimeSeconds);
        var keyed = (await _evidence.GetForGameAsync(gameId, includeDismissed: true))
            .FirstOrDefault(r => r.SourceKind == EvidenceKinds.TimelineRegion && r.SourceKey == sourceKey);
        if (keyed is not null)
        {
            if (string.IsNullOrWhiteSpace(keyed.Note))
            {
                await _evidence.DeleteBySourceKeyAsync(gameId, EvidenceKinds.TimelineRegion, sourceKey);
            }
            else
            {
                await _evidence.UpdateTitleAsync(keyed.Id, PatternConstants.ClearedDeathAuditTitle);
            }
            return;
        }

        // Promoted to a clip: keep the user's clip + note, retitle it out of the
        // death-class counts.
        if (await _evidence.FindPromotedDeathAuditAsync(gameId, gameTimeSeconds) is long promotedId)
        {
            await _evidence.UpdateTitleAsync(promotedId, PatternConstants.ClearedDeathAuditTitle);
        }
    }

    public async Task MaterializeReviewSignalsAsync(long gameId)
    {
        // Negative concept tags on the game -> one start-less anchor row per tag.
        // The detectors additionally EXISTS-join the live game_concept_tags row,
        // so an anchor left behind by an untag drops out of every count/playlist
        // without needing a delete here.
        var tagIds = await _conceptTags.GetIdsForGameAsync(gameId);
        if (tagIds.Count > 0)
        {
            var negativeTags = (await _conceptTags.GetAllAsync())
                .Where(static t => string.Equals(t.Polarity, "negative", StringComparison.OrdinalIgnoreCase))
                .Where(t => tagIds.Contains(t.Id));
            foreach (var tag in negativeTags)
            {
                await _evidence.UpsertAsync(new EvidenceUpsert(
                    GameId: gameId,
                    SourceKind: EvidenceKinds.TimelineRegion,
                    SourceId: null,
                    SourceKey: PatternConstants.TagSourceKey(tag.Id),
                    StartTimeSeconds: null,
                    EndTimeSeconds: null,
                    Title: tag.Name,
                    Polarity: EvidencePolarities.Bad,
                    Status: EvidenceStatuses.Evidence));
            }
        }

        // Rule break -> one anchor per game (same live-signal EXISTS shape:
        // clearing a false positive drops it from counts without a delete).
        var entry = await _sessionLog.GetEntryAsync(gameId);
        if (entry is { RuleBroken: > 0 })
        {
            await _evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.RuleBreakSourceKey,
                StartTimeSeconds: null,
                EndTimeSeconds: null,
                Title: PatternConstants.RuleBreakTitle,
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }
    }

    public async Task<int> BackfillWindowAsync()
    {
        var ids = await _games.GetPatternEvidenceBackfillIdsAsync(Version);
        var done = 0;
        foreach (var gameId in ids)
        {
            try
            {
                await MaterializeForGameAsync(gameId);
                done++;
            }
            catch (Exception ex)
            {
                // One bad game must never abort the pass; it stays unstamped and
                // retries on the next startup.
                _logger.LogError(ex, "Pattern-evidence backfill failed for game {GameId}", gameId);
            }
        }
        if (ids.Count > 0)
        {
            _logger.LogInformation(
                "Pattern-evidence backfill: {Done}/{Total} window games materialized (v{Version})",
                done, ids.Count, Version);
        }
        return done;
    }

    // Tolerant Details JSON probe (malformed details are skipped, never thrown).
    private static bool ReadDetailsBool(string? detailsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(detailsJson) || detailsJson == "{}")
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
