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
    /// so patterns match exactly what the objective timeline highlights. Fights
    /// anchor once per FIGHT (the shared <see cref="TeamfightClustering.Resolve"/> set:
    /// stored post-game rows with numbers, else the synthetic combat clusters), under
    /// every fight token the fight matches that an objective tracks — never per
    /// member event. Stale anchors (an event a correction moved or removed, a fight
    /// the post-game pass moved or dropped) are removed so a re-run converges.
    /// </summary>
    private async Task MaterializeTrackedEventAnchorsAsync(long gameId)
    {
        var ties = await _objectives.GetActiveObjectiveEventTokensAsync();
        var trackedTokens = new HashSet<string>(
            ties.Select(static t => PatternConstants.Canonical(t.Token)).Where(static t => t.Length > 0),
            StringComparer.Ordinal);

        var events = await _gameEvents.GetEventsAsync(gameId);

        // Every objev: key this game's events COULD produce, tracked or not. A token an
        // objective merely stopped tracking keeps its history because its keys stay live;
        // only an anchor no event produces any more (v3.11: a retimed or removed event,
        // a moved fight) is stale.
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (TeamfightClustering.IsStoredTeamfight(e))
            {
                continue; // a fight anchors once, per span, below
            }
            foreach (var rawToken in ObjectiveEventTieResolver.EventTokens(e))
            {
                var token = PatternConstants.Canonical(rawToken);
                var key = PatternConstants.ObjEventSourceKey(token, e.GameTimeS);
                liveKeys.Add(key);
                if (!trackedTokens.Contains(token))
                {
                    continue;
                }
                await UpsertAnchorAsync(
                    gameId,
                    sourceKey: key,
                    title: PatternConstants.TokenLabel(token),
                    startS: Math.Max(0, e.GameTimeS - PatternConstants.MomentLeadSeconds),
                    endS: e.GameTimeS + PatternConstants.MomentTrailSeconds,
                    polarity: PolarityFor(token));
            }
        }

        // Fights the player was NOT in never anchor: an anchor is a review moment in
        // the evidence inbox and a pattern-card count, and a fight elsewhere on the
        // map is neither — it is a timeline pin (and a marker for an objective
        // tracking ABSENT_TEAMFIGHT) and nothing more. Every key a fight COULD carry
        // counts as live (tracked or not) so the cleanup below removes only anchors
        // of fights that moved or vanished — never the history of a token an
        // objective merely stopped tracking, which every other anchor kind keeps.
        var claimedAnchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in TeamfightClustering.Resolve(events).Where(static s => s.IsOwn))
        {
            var anchor = TeamfightClustering.KeyAnchor(span, claimedAnchors);
            foreach (var rawToken in FightTokens(span))
            {
                var token = PatternConstants.Canonical(rawToken);
                var key = PatternConstants.ObjEventSourceKeyForToken(token) + anchor;
                liveKeys.Add(key);
                if (!trackedTokens.Contains(token))
                {
                    continue;
                }
                await UpsertAnchorAsync(
                    gameId,
                    sourceKey: key,
                    title: PatternConstants.TokenLabel(token),
                    startS: Math.Max(0, span.StartS - PatternConstants.TeamfightLeadSeconds),
                    endS: span.EndS + PatternConstants.TeamfightTrailSeconds,
                    polarity: PolarityFor(token));
            }
        }

        await CleanupStaleAnchorsAsync(gameId, liveKeys);
    }

    // The fight tokens a span matches: a stored row's own tokens (verdict + TEAMFIGHT,
    // or ABSENT_TEAMFIGHT), the plain TEAMFIGHT for a synthetic cluster.
    private static IReadOnlyList<string> FightTokens(TeamfightSpan span) =>
        span.Stored is { } stored
            ? ObjectiveEventTieResolver.EventTokens(stored)
            : [GameEvent.TrackableTokens.TeamfightToken];

    // Drop this game's objev: anchors whose key no event produces any more (a death a
    // correction retimed or removed, a fight that moved when the post-game pass replaced
    // the synthetic cluster, or vanished). Only UNTOUCHED default rows go (the shared
    // EvidenceAutoAnchors rule): anything the user noted, triaged (dismissed / highlighted /
    // re-judged), or attached to an objective, prompt, tag or matchup note is theirs to keep.
    private async Task CleanupStaleAnchorsAsync(long gameId, IReadOnlySet<string> liveKeys)
    {
        foreach (var row in await _evidence.GetForGameAsync(gameId, includeDismissed: true))
        {
            if (!row.SourceKey.StartsWith(PatternConstants.ObjEventSourceKeyPrefix, StringComparison.Ordinal)
                || liveKeys.Contains(row.SourceKey)
                || !EvidenceAutoAnchors.IsUntouched(row))
            {
                continue;
            }
            await _evidence.DeleteBySourceKeyAsync(gameId, EvidenceKinds.TimelineRegion, row.SourceKey);
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

    // Shared with EvidenceAutoAnchors (a polarity the user changed is a triage action).
    private static string PolarityFor(string token) => PatternConstants.DefaultAnchorPolarity(token);
}
