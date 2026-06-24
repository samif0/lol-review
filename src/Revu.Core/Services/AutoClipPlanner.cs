#nullable enable

using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// One planned auto-clip: the event it came from, the buffered window, and the
/// objective it ties to (for the bookmark tag + practiced side-effect).
/// </summary>
public readonly record struct PlannedClip(
    int EventId,
    int EventTimeS,
    int StartS,
    int EndS,
    long ObjectiveId,
    string ObjectiveTitle);

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
    ///   <item>Only events tied to an active objective are considered (via <paramref name="tieResolver"/>).</item>
    ///   <item>When <paramref name="objectiveId"/> is set, restrict to events tied to THAT objective.</item>
    ///   <item>Window: start = max(0, t - PreRoll), end = t + PostRoll, end clamped to
    ///         <paramref name="gameDurationS"/> when &gt; 0. Skipped if the window is &lt; 1s.</item>
    ///   <item>Dedupe: events whose source key is already in <paramref name="existingSourceKeys"/> are skipped.</item>
    ///   <item>Min-gap: an event whose buffered start is within <see cref="GameConstants.AutoClipMinGapS"/>
    ///         of the previous KEPT clip's start is skipped.</item>
    ///   <item>Cap: at most <see cref="GameConstants.AutoClipMaxPerCall"/> clips are returned.</item>
    /// </list>
    /// </summary>
    /// <param name="gameId">Used to form the per-event source key for dedupe.</param>
    /// <param name="skippedByCap">Outputs how many otherwise-eligible events were dropped by the cap.</param>
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

        var ties = tieResolver.ResolveForGame(events);

        // Ascending by time so the min-gap "previous kept clip" logic is meaningful.
        var ordered = events.OrderBy(e => e.GameTimeS).ThenBy(e => e.Id).ToList();

        int? lastKeptStart = null;
        foreach (var e in ordered)
        {
            if (!ties.TryGetValue(e.Id, out var eventTies) || eventTies.Count == 0) continue;

            // Restrict to the requested objective when framed; else take the first tie
            // (priority-lane winner) so each event is clipped once under one objective.
            ObjectiveTie tie;
            if (objectiveId is long want)
            {
                var match = eventTies.FirstOrDefault(t => t.ObjectiveId == want);
                if (match.ObjectiveId != want) continue; // not tied to the framed objective
                tie = match;
            }
            else
            {
                tie = eventTies[0];
            }

            var startS = Math.Max(0, e.GameTimeS - GameConstants.AutoClipPreRollS);
            var endS = e.GameTimeS + GameConstants.AutoClipPostRollS;
            if (gameDurationS > 0 && endS > gameDurationS) endS = gameDurationS;
            if (endS - startS < 1) continue; // degenerate window (event past game end)

            // Dedupe against already-persisted auto-clips for this game.
            var sourceKey = SourceKey(gameId, e.Id);
            if (existingSourceKeys.Contains(sourceKey)) continue;

            // Min-gap: collapse heavily-overlapping windows.
            if (lastKeptStart is int prev && startS - prev < GameConstants.AutoClipMinGapS) continue;

            if (planned.Count >= GameConstants.AutoClipMaxPerCall)
            {
                skippedByCap++;
                continue;
            }

            planned.Add(new PlannedClip(
                EventId: e.Id,
                EventTimeS: e.GameTimeS,
                StartS: startS,
                EndS: endS,
                ObjectiveId: tie.ObjectiveId,
                ObjectiveTitle: tie.Title));
            lastKeptStart = startS;
        }

        return planned;
    }

    /// <summary>The stable evidence dedupe key for an auto-clip of one event.</summary>
    public static string SourceKey(long gameId, int eventId) => $"autoclip:{gameId}:{eventId}";
}
