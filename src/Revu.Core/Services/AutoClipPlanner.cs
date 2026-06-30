#nullable enable

using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// One planned auto-clip: the moment it came from, the buffered window, the objective
/// it ties to (for the bookmark tag + practiced side-effect), and the stable evidence
/// <see cref="SourceKey"/> used to dedupe re-runs. A per-event clip carries the event's
/// id + time; a teamfight clip carries the cluster's start time as its anchor.
/// </summary>
public readonly record struct PlannedClip(
    int EventId,
    int EventTimeS,
    int StartS,
    int EndS,
    long ObjectiveId,
    string ObjectiveTitle,
    string SourceKey,
    bool IsTeamfight = false);

/// <summary>
/// PURE selection + window math for the on-demand objective auto-clipper, split out
/// so it is unit-testable without a database or ffmpeg. Decides WHICH events become
/// clips and over WHAT window; the service does the IO (extract + persist).
/// </summary>
public static class AutoClipPlanner
{
    /// <summary>
    /// Plan the clips for a game's events.
    ///
    /// <para>Rules (all from <see cref="GameConstants"/>):</para>
    /// <list type="bullet">
    ///   <item>TEAMFIGHTS clip as ONE moment per fight, spanning the cluster (first→last
    ///         combat event) plus the buffer — NOT one clip per kill/death/assist inside
    ///         it. (Previously each member event was clipped, flooding the folder.)</item>
    ///   <item>A combat event is clipped INDIVIDUALLY only when an objective tracks its
    ///         own token (KILL/DEATH/…). A kill/death/assist tied to an objective ONLY by
    ///         teamfight membership is covered by the fight clip and is not re-clipped.</item>
    ///   <item>When <paramref name="objectiveId"/> is set, restrict to clips tied to THAT objective.</item>
    ///   <item>Window: start = max(0, t - PreRoll), end = t + PostRoll (per-event) or the
    ///         cluster end + PostRoll (teamfight), clamped to <paramref name="gameDurationS"/>
    ///         when &gt; 0. Skipped if the window is &lt; 1s.</item>
    ///   <item>Dedupe: clips whose source key is already in <paramref name="existingSourceKeys"/> are skipped.</item>
    ///   <item>Min-gap: a clip whose buffered start is within <see cref="GameConstants.AutoClipMinGapS"/>
    ///         of the previous KEPT clip's start is skipped.</item>
    ///   <item>Cap: at most <see cref="GameConstants.AutoClipMaxPerCall"/> clips are returned.</item>
    /// </list>
    /// </summary>
    /// <param name="gameId">Used to form the source keys for dedupe.</param>
    /// <param name="skippedByCap">Outputs how many otherwise-eligible clips were dropped by the cap.</param>
    public static IReadOnlyList<PlannedClip> SelectClips(
        long gameId,
        IReadOnlyList<GameEvent> events,
        ObjectiveEventTieResolver tieResolver,
        long? objectiveId,
        int gameDurationS,
        IReadOnlySet<string> existingSourceKeys,
        out int skippedByCap)
    {
        skippedByCap = 0;
        var planned = new List<PlannedClip>();
        if (events.Count == 0 || tieResolver.IsEmpty) return planned;

        bool WantsObjective(long id) => objectiveId is not long want || id == want;

        // ── Candidate clips, each as (anchorTime, start, end, objective, key) ──────────
        // We gather TEAMFIGHT clips (one per cluster) and per-event clips, then apply
        // dedupe / min-gap / cap uniformly across the merged, time-ordered set.
        var candidates = new List<PlannedClip>();

        // 1) One clip per teamfight cluster, spanning the whole fight.
        var clusters = tieResolver.ResolveTeamfightClusters(events);
        // Event ids covered by a fight, PER objective — so a combat event tied to obj X by
        // teamfight membership isn't also clipped individually under X.
        var coveredByFight = new Dictionary<long, HashSet<int>>();
        foreach (var c in clusters)
        {
            foreach (var tie in c.Objectives)
            {
                if (!coveredByFight.TryGetValue(tie.ObjectiveId, out var set))
                    coveredByFight[tie.ObjectiveId] = set = new HashSet<int>();
                foreach (var id in c.MemberEventIds) set.Add(id);

                if (!WantsObjective(tie.ObjectiveId)) continue;

                var startS = Math.Max(0, c.StartS - GameConstants.AutoClipPreRollS);
                var endS = c.EndS + GameConstants.AutoClipPostRollS;
                if (gameDurationS > 0 && endS > gameDurationS) endS = gameDurationS;
                if (endS - startS < 1) continue;

                candidates.Add(new PlannedClip(
                    EventId: c.MemberEventIds.Count > 0 ? c.MemberEventIds[0] : 0,
                    EventTimeS: c.StartS,
                    StartS: startS,
                    EndS: endS,
                    ObjectiveId: tie.ObjectiveId,
                    ObjectiveTitle: tie.Title,
                    SourceKey: TeamfightSourceKey(gameId, c.StartS),
                    IsTeamfight: true));
            }
        }

        // 2) Per-event clips — ONLY for events tied by their own token (not teamfight),
        //    and not already inside a fight of the same objective.
        foreach (var e in events)
        {
            foreach (var tie in tieResolver.TokenTiesForEvent(e))
            {
                if (!WantsObjective(tie.ObjectiveId)) continue;
                if (coveredByFight.TryGetValue(tie.ObjectiveId, out var fightSet) && fightSet.Contains(e.Id))
                    continue; // covered by the fight clip

                var startS = Math.Max(0, e.GameTimeS - GameConstants.AutoClipPreRollS);
                var endS = e.GameTimeS + GameConstants.AutoClipPostRollS;
                if (gameDurationS > 0 && endS > gameDurationS) endS = gameDurationS;
                if (endS - startS < 1) continue;

                candidates.Add(new PlannedClip(
                    EventId: e.Id,
                    EventTimeS: e.GameTimeS,
                    StartS: startS,
                    EndS: endS,
                    ObjectiveId: tie.ObjectiveId,
                    ObjectiveTitle: tie.Title,
                    SourceKey: SourceKey(gameId, e.Id)));

                // When unframed, an event is clipped once under its priority objective —
                // mirror the old "first tie wins" so we don't fan one event across every
                // objective that tracks it.
                if (objectiveId is null) break;
            }
        }

        // ── Apply dedupe / min-gap / cap across the merged set, time-ordered ───────────
        var ordered = candidates
            .OrderBy(c => c.StartS).ThenBy(c => c.EventTimeS).ThenBy(c => c.EventId)
            .ToList();

        int? lastKeptEnd = null;
        var keptKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ordered)
        {
            // Dedupe against already-persisted auto-clips AND against earlier kept clips
            // this run (two clusters can't collide, but a defensive guard is cheap).
            if (existingSourceKeys.Contains(c.SourceKey) || !keptKeys.Add(c.SourceKey)) continue;

            // Collapse near-duplicate windows by COVERAGE, not raw start proximity. Drop a
            // clip only when it adds little new footage past the last kept clip — i.e. its
            // window ends no more than AutoClipMinGapS beyond the last kept end. Keying on
            // StartS alone was wrong: every clip subtracts the 30s pre-roll, so two DISTINCT
            // moments (two separate teamfights, or a lone event just before a fight) whose
            // anchors are ~15-19s apart had near-equal starts and the later — longer —
            // window was silently dropped. A teamfight cluster is always its own moment, so
            // it never collapses into a neighbor.
            if (!c.IsTeamfight
                && lastKeptEnd is int prevEnd
                && c.EndS <= prevEnd + GameConstants.AutoClipMinGapS)
            {
                continue;
            }

            if (planned.Count >= GameConstants.AutoClipMaxPerCall) { skippedByCap++; continue; }

            planned.Add(c);
            // Advance the high-water end so a chain of overlapping clips collapses against
            // the furthest extent kept so far, not just the immediately previous one.
            lastKeptEnd = lastKeptEnd is int e ? Math.Max(e, c.EndS) : c.EndS;
        }

        return planned;
    }

    /// <summary>The stable evidence dedupe key for an auto-clip of one event.</summary>
    public static string SourceKey(long gameId, int eventId) => $"autoclip:{gameId}:{eventId}";

    /// <summary>The stable dedupe key for a per-teamfight auto-clip (anchored on the
    /// cluster's start second, which is stable across re-runs of the same game).</summary>
    public static string TeamfightSourceKey(long gameId, int clusterStartS) => $"autoclip-tf:{gameId}:{clusterStartS}";
}
