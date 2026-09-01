#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Produces the evidence rows the objective-driven pattern detectors count:
/// one anchor per tracked-event occurrence (<c>objev:{TOKEN}:{t}</c>, matching
/// the ObjectiveEventTieResolver semantics the timeline and auto-clipper use)
/// and one anchor per failed structured criterion per game
/// (<c>objcrit:{objectiveId}</c>). Runs automatically at game end, after a
/// review save, and via a windowed startup backfill for games already in the
/// DB (stamped per game in games.pattern_evidence_v).
///
/// <para>
/// Version 2 (v3.6): patterns became objective-only. Materialization also
/// CLEANS UP the retired v3.5 rows (gank deaths, death audits, tag anchors,
/// rule breaks, inferred regions) — except anything the user noted or promoted
/// to a clip, which is preserved.
/// </para>
/// </summary>
public interface IPatternEvidenceMaterializer
{
    /// <summary>Materialize one game's pattern evidence (tracked-event anchors,
    /// failed-criterion anchors, retired-row cleanup) and stamp
    /// games.pattern_evidence_v.</summary>
    Task MaterializeForGameAsync(long gameId);

    /// <summary>Refresh the game's failed-criterion anchors (called after a
    /// review save, whose objective practices can change criteria outcomes;
    /// also part of <see cref="MaterializeForGameAsync"/>).</summary>
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
    /// MapStateAnalyzer.Version contract). v2 = objective-only anchors + retired-row cleanup.</summary>
    public const int Version = 2;

    private readonly IGameEventsRepository _gameEvents;
    private readonly IEvidenceRepository _evidence;
    private readonly IObjectivesRepository _objectives;
    private readonly IGameRepository _games;
    private readonly ILogger<PatternEvidenceMaterializer> _logger;

    public PatternEvidenceMaterializer(
        IGameEventsRepository gameEvents,
        IEvidenceRepository evidence,
        IObjectivesRepository objectives,
        IGameRepository games,
        ILogger<PatternEvidenceMaterializer> logger)
    {
        _gameEvents = gameEvents;
        _evidence = evidence;
        _objectives = objectives;
        _games = games;
        _logger = logger;
    }

    public async Task MaterializeForGameAsync(long gameId)
    {
        await CleanupRetiredRowsAsync(gameId);
        await MaterializeTrackedEventAnchorsAsync(gameId);
        await MaterializeReviewSignalsAsync(gameId);
        await _games.UpdatePatternEvidenceVersionAsync(gameId, Version);
    }

    /// <summary>
    /// One anchor per (tracked token, event second), for tokens any ACTIVE
    /// objective tracks — tie semantics via ObjectiveEventTieResolver.EventTokens
    /// so patterns match exactly what the objective timeline highlights. When
    /// TEAMFIGHT is tracked, one anchor per combat CLUSTER (≥3 combat events
    /// chained within 14s), not per member event.
    /// </summary>
    private async Task MaterializeTrackedEventAnchorsAsync(long gameId)
    {
        var ties = await _objectives.GetActiveObjectiveEventTokensAsync();
        if (ties.Count == 0)
        {
            return;
        }
        var trackedTokens = new HashSet<string>(
            ties.Select(static t => PatternConstants.Canonical(t.Token)).Where(static t => t.Length > 0),
            StringComparer.Ordinal);
        if (trackedTokens.Count == 0)
        {
            return;
        }

        var events = await _gameEvents.GetEventsAsync(gameId);

        foreach (var e in events)
        {
            foreach (var rawToken in ObjectiveEventTieResolver.EventTokens(e))
            {
                var token = PatternConstants.Canonical(rawToken);
                if (!trackedTokens.Contains(token))
                {
                    continue;
                }
                await UpsertAnchorAsync(
                    gameId,
                    sourceKey: PatternConstants.ObjEventSourceKey(token, e.GameTimeS),
                    title: PatternConstants.TokenLabel(token),
                    startS: Math.Max(0, e.GameTimeS - PatternConstants.MomentLeadSeconds),
                    endS: e.GameTimeS + PatternConstants.MomentTrailSeconds,
                    polarity: PolarityFor(token));
            }
        }

        if (trackedTokens.Contains(GameEvent.TrackableTokens.TeamfightToken))
        {
            foreach (var (start, end) in TeamfightClusters(events))
            {
                await UpsertAnchorAsync(
                    gameId,
                    sourceKey: PatternConstants.ObjEventSourceKey(GameEvent.TrackableTokens.TeamfightToken, start),
                    title: PatternConstants.TokenLabel(GameEvent.TrackableTokens.TeamfightToken),
                    startS: Math.Max(0, start - PatternConstants.TeamfightLeadSeconds),
                    endS: end + PatternConstants.TeamfightTrailSeconds,
                    polarity: EvidencePolarities.Neutral);
            }
        }
    }

    public async Task MaterializeReviewSignalsAsync(long gameId)
    {
        // One start-less anchor per objective whose structured criterion failed
        // for this game. The detectors additionally EXISTS-gate on the live
        // game_objectives row, so a later re-evaluation that passes (or
        // archiving the objective) drops the anchor from every count/playlist
        // without needing a delete here.
        foreach (var go in await _objectives.GetGameObjectivesAsync(gameId))
        {
            if (go.CriteriaMet != 0)
            {
                continue; // passed (1) or never evaluated (null)
            }
            await _evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: PatternConstants.ObjCritSourceKey(go.ObjectiveId),
                StartTimeSeconds: null,
                EndTimeSeconds: null,
                Title: PatternConstants.ObjCritTitle(string.IsNullOrWhiteSpace(go.Title) ? "objective" : go.Title),
                Polarity: EvidencePolarities.Bad,
                // 'evidence', not 'needs_review': materialized rows must never
                // flood the review queue's pending count.
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

    /// <summary>
    /// Remove this game's retired v3.5 materialized rows (they fed detectors
    /// that no longer exist and would otherwise linger in the per-game evidence
    /// list). Preserved: rows carrying a user note, and rows promoted to clips
    /// (those are source_kind 'clip' now and never match the retired keys).
    /// </summary>
    private async Task CleanupRetiredRowsAsync(long gameId)
    {
        foreach (var row in await _evidence.GetForGameAsync(gameId, includeDismissed: true))
        {
            if (row.SourceKind != EvidenceKinds.TimelineRegion
                || !string.IsNullOrWhiteSpace(row.Note)
                || !IsRetiredSourceKey(row.SourceKey))
            {
                continue;
            }
            await _evidence.DeleteBySourceKeyAsync(gameId, EvidenceKinds.TimelineRegion, row.SourceKey);
        }
    }

    private static bool IsRetiredSourceKey(string sourceKey)
    {
        if (PatternConstants.RetiredSourceKeys.Contains(sourceKey, StringComparer.Ordinal))
        {
            return true;
        }
        foreach (var prefix in PatternConstants.RetiredSourceKeyPrefixes)
        {
            if (sourceKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Anchor upsert with the promoted-twin guard: a moment the note
    /// flow promoted to a clip was rekeyed out from under its source key (title
    /// and exact window preserved), so a re-run must not re-insert it.</summary>
    private async Task UpsertAnchorAsync(
        long gameId, string sourceKey, string title, int startS, int endS, string polarity)
    {
        if (await _evidence.FindPromotedTwinAsync(gameId, title, startS, endS) is not null)
        {
            return;
        }
        await _evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: sourceKey,
            StartTimeSeconds: startS,
            EndTimeSeconds: endS,
            Title: title,
            Polarity: polarity,
            Status: EvidenceStatuses.Evidence));
    }

    // Deaths (and their derived gank/fog attributes) read as bad; everything
    // else a player might track (kills, objectives, trades, casts) is neutral —
    // recurrence is the signal, not blame.
    private static string PolarityFor(string token) => token switch
    {
        GameEvent.EventTypes.Death => EvidencePolarities.Bad,
        GameEvent.TrackableTokens.JungleGankToken => EvidencePolarities.Bad,
        GameEvent.TrackableTokens.FogDeathToken => EvidencePolarities.Bad,
        _ => EvidencePolarities.Neutral,
    };

    /// <summary>Combat clusters per the teamfight membership rule the tie
    /// resolver applies (≥3 combat events, consecutive gaps ≤14s, t&gt;0).</summary>
    private static IEnumerable<(int Start, int End)> TeamfightClusters(IReadOnlyList<GameEvent> events)
    {
        var combat = events
            .Where(static e => IsCombat(e.EventType) && e.GameTimeS > 0)
            .Select(static e => e.GameTimeS)
            .OrderBy(static t => t)
            .ToList();

        var i = 0;
        while (i < combat.Count)
        {
            var j = i;
            while (j + 1 < combat.Count && combat[j + 1] - combat[j] <= PatternConstants.TeamfightGapSeconds)
            {
                j++;
            }
            if (j - i + 1 >= PatternConstants.TeamfightMinEvents)
            {
                yield return (combat[i], combat[j]);
            }
            i = j + 1;
        }
    }

    private static bool IsCombat(string eventType) =>
        eventType.Equals(GameEvent.EventTypes.Kill, StringComparison.OrdinalIgnoreCase)
        || eventType.Equals(GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase)
        || eventType.Equals(GameEvent.EventTypes.Assist, StringComparison.OrdinalIgnoreCase)
        || eventType.Equals(GameEvent.EventTypes.FirstBlood, StringComparison.OrdinalIgnoreCase)
        || eventType.Equals(GameEvent.EventTypes.MultiKill, StringComparison.OrdinalIgnoreCase);
}
