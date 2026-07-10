#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>Result of one game's map-state pass: new JUNGLE_PROXIMITY rows to insert
/// (GameId left 0 — the caller owns row identity) and the stored DEATH rows whose
/// Details were stamped in place (so the caller can persist exactly those).</summary>
public sealed record MapStateAnalysis(
    IReadOnlyList<GameEvent> ProximityEvents,
    IReadOnlyList<GameEvent> StampedDeaths)
{
    public static readonly MapStateAnalysis Empty = new([], []);
}

/// <summary>
/// v3.2: post-game map-state analysis over the Match-V5 timeline. The timeline's
/// participantFrames carry every player's x/y once a minute (plus exact positions on
/// kill/objective/building events), so two derived signals become computable:
///
/// <para>1. JUNGLE_PROXIMITY events — closest-approach windows where the enemy or
/// ally jungler came inside the gank-threat radius of the player during laning.
/// Both participants' tracks are piecewise-linear through their samples (frames +
/// positioned events), swept every <see cref="SweepStepMs"/> and at every knot, with
/// consecutive in-radius instants merged into ONE event per visit (anchored at the
/// visit's start; Details carries the closest distance + duration). Sweeping the
/// interpolated tracks — not just frame instants — is what catches a gank arriving
/// BETWEEN frames whenever any positioned event pins the jungler's path. Death →
/// respawn teleports are blacked out so the interpolation can't sweep a phantom
/// track across the map. Details.who says whose jungler ("enemy" = danger window,
/// "ally" = play-aggressive window). GOD-VIEW: this records where the jungler
/// actually WAS, not what the player could see — it anchors the review question,
/// it never claims "you knew this".</para>
///
/// <para>2. DEATH stamping — each stored DEATH row (matched to its timeline
/// CHAMPION_KILL by time) gains the interpolated enemy/ally jungler distance at death,
/// plus <c>enemy_jg_dark_s</c>: how long the enemy jungler had gone without a
/// map-visible reveal (a positioned kill/objective/building event involving him — the
/// events the game announces to everyone). When he was on the kill after being dark
/// for <see cref="FogDarkSeconds"/>+, the death is stamped <c>fog_death</c> — "died
/// into fog without information" — the knowledge-side complement to the god-view
/// distances. Mirrors <see cref="JungleGankClassifier"/>: attribute on the existing
/// DEATH row, matching FOG_DEATH token derived at read time, no double-counting.</para>
///
/// Pure and DB-free: the caller fetches the match + timeline payloads and the stored
/// events, and persists whatever comes back.
/// </summary>
public static class MapStateAnalyzer
{
    /// <summary>Analyzer version persisted to games.map_state_v — bump when the
    /// detection logic changes enough that old games deserve a re-run.
    /// v2: proximity moved from frame-instant checks to the interpolated-track
    /// sweep with closest-approach clustering — a gank arriving BETWEEN frames
    /// (observed live: enemy jungler collapsed on the lane mid-minute and v1 saw
    /// nothing) is caught whenever a positioned event pins the jungler's path.</summary>
    public const int Version = 2;

    /// <summary>Gank-threat radius in map units (the map is ~14,870 units square; a
    /// screen is ~2,400). Inside this, a jungler is one rotation from being on you.</summary>
    public const int ThreatRadiusUnits = 4000;

    /// <summary>Seconds the enemy jungler must have been unrevealed before a death he
    /// participated in reads as a fog death (one full frame interval).</summary>
    public const int FogDarkSeconds = 60;

    // Skip the spawn/leash frames: at 0:00–2:00 everyone stands near fountain or a
    // leashed camp, so "jungler within radius" is trivially true and pure noise.
    private const long ProximityStartMs = 120_000;

    // A stored DEATH (live kill-feed clock) matches a timeline CHAMPION_KILL
    // (server clock) within this many seconds — the two clocks skew slightly.
    private const int DeathMatchToleranceS = 25;

    // Max distance in time from the nearest position sample before interpolation
    // gives up and reports no distance (1.5 frame intervals). Death-stamp only —
    // the proximity sweep uses strict bracketing instead (see PositionAtStrict).
    private const long MaxSampleGapMs = 90_000;

    // v2 sweep: evaluate the interpolated self↔jungler distance on this grid (plus
    // every frame/event knot), and merge hits within this gap into one visit.
    private const long SweepStepMs = 15_000;
    private const long ClusterGapMs = 30_000;

    private readonly record struct Sample(long TMs, double X, double Y);
    private readonly record struct SweepHit(long TMs, double Dist, double SelfX, double SelfY, double JgX, double JgY);
    private sealed record TimelineDeath(long TMs, double X, double Y, int KillerId, IReadOnlyList<int> AssistIds);
    private sealed record Roster(int SelfId, int? EnemyJgId, string EnemyJgChampion, int? AllyJgId, string AllyJgChampion);

    /// <summary>
    /// Run the full pass. Returns <see cref="MapStateAnalysis.Empty"/> (and stamps
    /// nothing) when the player isn't in the match, the timeline has no frames, or no
    /// jungler can be identified (ARAM, remakes) — better to derive nothing than to
    /// guess. DEATH rows in <paramref name="storedEvents"/> are mutated in place.
    /// </summary>
    public static MapStateAnalysis Analyze(
        JsonElement match, JsonElement timeline, string puuid, IReadOnlyList<GameEvent> storedEvents)
    {
        var roster = ResolveRoster(match, puuid);
        if (roster is null || (roster.EnemyJgId is null && roster.AllyJgId is null))
            return MapStateAnalysis.Empty;

        if (!timeline.TryGetProperty("info", out var info)
            || !info.TryGetProperty("frames", out var frames)
            || frames.ValueKind != JsonValueKind.Array)
            return MapStateAnalysis.Empty;

        var samples = new Dictionary<int, List<Sample>>();
        var frameTimes = new List<long>();
        var enemyReveals = new List<long>();
        var timelineDeaths = new List<TimelineDeath>();
        var trackedDeaths = new Dictionary<int, List<long>>();

        foreach (var frame in frames.EnumerateArray())
        {
            var frameTs = Long(frame, "timestamp");
            frameTimes.Add(frameTs);
            CollectFrameSamples(frame, frameTs, roster, samples);
            CollectEventSamplesRevealsAndDeaths(frame, roster, samples, enemyReveals, timelineDeaths, trackedDeaths);
        }

        foreach (var list in samples.Values)
            list.Sort(static (a, b) => a.TMs.CompareTo(b.TMs));
        frameTimes.Sort();
        enemyReveals.Sort();
        timelineDeaths.Sort(static (a, b) => a.TMs.CompareTo(b.TMs));

        var blackouts = BuildBlackouts(trackedDeaths, frameTimes);
        var proximity = SweepProximity(roster, samples, blackouts, frameTimes);
        var stamped = StampDeaths(storedEvents, roster, samples, enemyReveals, timelineDeaths);
        return new MapStateAnalysis(proximity, stamped);
    }

    // ── roster ──────────────────────────────────────────────────────────────

    // Self + both junglers from the MATCH payload (participantId, teamId,
    // teamPosition, championName) — the same resolution path as
    // EnemyLanerBackfillService. Null when the player isn't in the match.
    private static Roster? ResolveRoster(JsonElement match, string puuid)
    {
        if (!match.TryGetProperty("info", out var info)
            || !info.TryGetProperty("participants", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
            return null;

        int selfId = 0, selfTeam = 0;
        foreach (var p in parts.EnumerateArray())
        {
            if (string.Equals(Str(p, "puuid"), puuid, StringComparison.OrdinalIgnoreCase))
            {
                selfId = Int(p, "participantId");
                selfTeam = Int(p, "teamId");
                break;
            }
        }
        if (selfId <= 0 || selfTeam == 0) return null;

        int? enemyJg = null, allyJg = null;
        string enemyCh = "", allyCh = "";
        foreach (var p in parts.EnumerateArray())
        {
            if (Str(p, "teamPosition") != "JUNGLE") continue;
            var id = Int(p, "participantId");
            if (id <= 0) continue;
            var team = Int(p, "teamId");
            if (team == selfTeam && id != selfId) { allyJg = id; allyCh = Str(p, "championName"); }
            else if (team != 0 && team != selfTeam) { enemyJg = id; enemyCh = Str(p, "championName"); }
        }
        return new Roster(selfId, enemyJg, enemyCh, allyJg, allyCh);
    }

    // ── frame pass ──────────────────────────────────────────────────────────

    // One frame: record position samples for the three tracked participants.
    private static void CollectFrameSamples(
        JsonElement frame, long frameTs, Roster roster, Dictionary<int, List<Sample>> samples)
    {
        if (!frame.TryGetProperty("participantFrames", out var pFrames)
            || pFrames.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in pFrames.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var pid)) continue;
            if (pid != roster.SelfId && pid != roster.EnemyJgId && pid != roster.AllyJgId) continue;
            if (!TryReadPosition(prop.Value, out var x, out var y)) continue;
            AddSample(samples, pid, new Sample(frameTs, x, y));
        }
    }

    // ── event pass ──────────────────────────────────────────────────────────

    // Positioned timeline events refine the once-a-minute samples with exact
    // positions, and double as the REVEAL record: a champion kill (killer/victim) or
    // an objective/building take (killer) is announced or map-visible to both teams,
    // so it marks a moment the enemy jungler's position was public knowledge.
    // Assisters are NOT reveals (a cross-map ult assist shows nothing on the map),
    // but they DO count as "on the kill" for fog deaths, matching the gank rule.
    // Tracked participants' own deaths are recorded for the respawn blackouts.
    private static void CollectEventSamplesRevealsAndDeaths(
        JsonElement frame, Roster roster,
        Dictionary<int, List<Sample>> samples, List<long> enemyReveals,
        List<TimelineDeath> deaths, Dictionary<int, List<long>> trackedDeaths)
    {
        if (!frame.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return;

        foreach (var ev in events.EnumerateArray())
        {
            var type = Str(ev, "type");
            var isKill = type == "CHAMPION_KILL";
            var isTake = type is "ELITE_MONSTER_KILL" or "BUILDING_KILL" or "TURRET_PLATE_DESTROYED";
            if (!isKill && !isTake) continue;
            if (!ev.TryGetProperty("position", out var posEl)
                || !TryReadPosition(posEl, out var x, out var y)) continue;

            var ts = Long(ev, "timestamp");
            var killerId = Int(ev, "killerId");
            RecordParticipantAt(roster, samples, enemyReveals, killerId, ts, x, y);

            if (!isKill) continue;
            var victimId = Int(ev, "victimId");
            RecordParticipantAt(roster, samples, enemyReveals, victimId, ts, x, y);

            if (IsTracked(roster, victimId))
            {
                if (!trackedDeaths.TryGetValue(victimId, out var list)) { list = []; trackedDeaths[victimId] = list; }
                list.Add(ts);
            }

            if (victimId == roster.SelfId)
                deaths.Add(new TimelineDeath(ts, x, y, killerId, ReadIntArray(ev, "assistingParticipantIds")));
        }
    }

    private static bool IsTracked(Roster roster, int pid) =>
        pid == roster.SelfId || pid == roster.EnemyJgId || pid == roster.AllyJgId;

    // TryReadPosition helper split out so a frame's participantFrames entry (position
    // nested under "position") and an event (same shape) share one parser.
    private static bool TryReadPosition(JsonElement holder, out double x, out double y)
    {
        x = y = 0;
        var pos = holder;
        if (holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("position", out var nested))
            pos = nested;
        if (pos.ValueKind != JsonValueKind.Object) return false;
        if (!pos.TryGetProperty("x", out var xEl) || xEl.ValueKind != JsonValueKind.Number) return false;
        if (!pos.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.Number) return false;
        x = xEl.GetDouble();
        y = yEl.GetDouble();
        return true;
    }

    private static void RecordParticipantAt(
        Roster roster, Dictionary<int, List<Sample>> samples, List<long> enemyReveals,
        int pid, long ts, double x, double y)
    {
        if (pid <= 0) return;
        if (IsTracked(roster, pid))
            AddSample(samples, pid, new Sample(ts, x, y));
        if (pid == roster.EnemyJgId)
            enemyReveals.Add(ts);
    }

    // ── proximity sweep (v2) ────────────────────────────────────────────────

    // After a tracked participant dies, his next sample is the respawn fountain —
    // interpolating across that teleport would sweep a phantom track through the
    // middle of the map. Black out from each death until the next frame pins the
    // participant again (fallback: one frame interval).
    private static Dictionary<int, List<(long Start, long End)>> BuildBlackouts(
        Dictionary<int, List<long>> trackedDeaths, List<long> frameTimes)
    {
        var result = new Dictionary<int, List<(long, long)>>();
        foreach (var (pid, deathTimes) in trackedDeaths)
        {
            var windows = new List<(long, long)>();
            foreach (var d in deathTimes)
            {
                var end = d + 60_000;
                foreach (var ft in frameTimes)
                {
                    if (ft > d) { end = ft; break; }
                }
                windows.Add((d, end));
            }
            result[pid] = windows;
        }
        return result;
    }

    private static bool InBlackout(
        Dictionary<int, List<(long Start, long End)>> blackouts, int pid, long t) =>
        blackouts.TryGetValue(pid, out var windows) && windows.Any(w => t > w.Start && t < w.End);

    // Evaluate self↔jungler distance across the laning window on the sweep grid plus
    // every frame/event knot, then merge consecutive in-radius instants into ONE
    // event per visit: anchored at the visit's start (where the review scrub should
    // begin), Details carrying the closest approach and the visit duration.
    private static List<GameEvent> SweepProximity(
        Roster roster,
        Dictionary<int, List<Sample>> samples,
        Dictionary<int, List<(long Start, long End)>> blackouts,
        List<long> frameTimes)
    {
        var proximity = new List<GameEvent>();
        if (!samples.TryGetValue(roster.SelfId, out var selfTrack)) return proximity;

        var windowStart = ProximityStartMs;
        var windowEnd = JungleGankClassifier.LaningPhaseEndSeconds * 1000L;

        foreach (var (jgId, who, champion) in new[]
        {
            (roster.EnemyJgId, "enemy", roster.EnemyJgChampion),
            (roster.AllyJgId, "ally", roster.AllyJgChampion),
        })
        {
            if (jgId is not { } id || !samples.TryGetValue(id, out var jgTrack)) continue;

            var times = new SortedSet<long>();
            for (var t = windowStart; t <= windowEnd; t += SweepStepMs) times.Add(t);
            foreach (var ft in frameTimes)
                if (ft >= windowStart && ft <= windowEnd) times.Add(ft);
            foreach (var s in jgTrack)
                if (s.TMs >= windowStart && s.TMs <= windowEnd) times.Add(s.TMs);

            var hits = new List<SweepHit>();
            foreach (var t in times)
            {
                if (InBlackout(blackouts, roster.SelfId, t) || InBlackout(blackouts, id, t)) continue;
                if (PositionAtStrict(selfTrack, t) is not { } posSelf) continue;
                if (PositionAtStrict(jgTrack, t) is not { } posJg) continue;
                var dist = Distance(posSelf, posJg);
                if (dist <= ThreatRadiusUnits)
                    hits.Add(new SweepHit(t, dist, posSelf.X, posSelf.Y, posJg.X, posJg.Y));
            }

            var from = 0;
            for (var i = 1; i <= hits.Count; i++)
            {
                if (i < hits.Count && hits[i].TMs - hits[i - 1].TMs <= ClusterGapMs) continue;
                EmitVisit(proximity, who, champion, hits, from, i - 1);
                from = i;
            }
        }

        proximity.Sort(static (a, b) => a.GameTimeS.CompareTo(b.GameTimeS));
        return proximity;
    }

    private static void EmitVisit(
        List<GameEvent> proximity, string who, string champion,
        List<SweepHit> hits, int from, int to)
    {
        if (to < from) return;
        var closest = hits[from];
        for (var i = from + 1; i <= to; i++)
            if (hits[i].Dist < closest.Dist) closest = hits[i];

        var details = new JsonObject
        {
            ["who"] = who,
            ["champion"] = champion,
            ["distance"] = (int)Math.Round(closest.Dist),
            ["duration_s"] = (int)((hits[to].TMs - hits[from].TMs) / 1000),
            ["self_x"] = (int)closest.SelfX,
            ["self_y"] = (int)closest.SelfY,
            ["jg_x"] = (int)closest.JgX,
            ["jg_y"] = (int)closest.JgY,
            ["detected"] = true,
        };
        proximity.Add(new GameEvent
        {
            EventType = GameEvent.EventTypes.JungleProximity,
            GameTimeS = (int)(hits[from].TMs / 1000),
            Details = details.ToJsonString(),
        });
    }

    // Strict interpolation for the sweep: the instant must sit INSIDE the sample
    // span (bracketing samples; an exact knot answers as itself). No nearest-clamp
    // extrapolation — a lone sighting must not smear across ±90s of sweep. The
    // clamped PositionAt below stays for death stamping, where a nearest sample
    // is an acceptable answer for a single instant.
    private static (double X, double Y)? PositionAtStrict(List<Sample> sorted, long tMs)
    {
        Sample? before = null, after = null;
        foreach (var s in sorted)
        {
            if (s.TMs <= tMs) before = s;
            if (s.TMs >= tMs) { after = s; break; }
        }
        if (before is not { } b || after is not { } a) return null;
        if (a.TMs == b.TMs) return (b.X, b.Y);
        var f = (double)(tMs - b.TMs) / (a.TMs - b.TMs);
        return (b.X + (a.X - b.X) * f, b.Y + (a.Y - b.Y) * f);
    }

    // ── death stamping ──────────────────────────────────────────────────────

    // Match each stored DEATH row (live kill-feed clock) to its nearest unclaimed
    // timeline CHAMPION_KILL and stamp jungler distances + darkness onto its Details.
    private static IReadOnlyList<GameEvent> StampDeaths(
        IReadOnlyList<GameEvent> storedEvents, Roster roster,
        Dictionary<int, List<Sample>> samples, List<long> enemyReveals, List<TimelineDeath> timelineDeaths)
    {
        var stamped = new List<GameEvent>();
        if (storedEvents.Count == 0 || timelineDeaths.Count == 0) return stamped;

        var claimed = new bool[timelineDeaths.Count];
        foreach (var e in storedEvents.OrderBy(static e => e.GameTimeS))
        {
            if (!string.Equals(e.EventType, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
                continue;

            var best = -1;
            var bestDelta = int.MaxValue;
            for (var i = 0; i < timelineDeaths.Count; i++)
            {
                if (claimed[i]) continue;
                var delta = Math.Abs((int)(timelineDeaths[i].TMs / 1000) - e.GameTimeS);
                if (delta < bestDelta) { best = i; bestDelta = delta; }
            }
            if (best < 0 || bestDelta > DeathMatchToleranceS) continue;
            claimed[best] = true;

            if (TryStampDeath(e, timelineDeaths[best], roster, samples, enemyReveals))
                stamped.Add(e);
        }
        return stamped;
    }

    private static bool TryStampDeath(
        GameEvent death, TimelineDeath at, Roster roster,
        Dictionary<int, List<Sample>> samples, List<long> enemyReveals)
    {
        try
        {
            var node = string.IsNullOrWhiteSpace(death.Details) || death.Details == "{}"
                ? new JsonObject()
                : JsonNode.Parse(death.Details) as JsonObject ?? new JsonObject();

            node["map_state"] = true;

            var enemyDist = JunglerDistanceAt(roster.EnemyJgId, at, samples);
            if (enemyDist is { } ed) node["enemy_jg_dist"] = ed;
            var allyDist = JunglerDistanceAt(roster.AllyJgId, at, samples);
            if (allyDist is { } ad) node["ally_jg_dist"] = ad;

            if (roster.EnemyJgId is { } enemyId)
            {
                // Darkness: seconds since the last map-visible reveal strictly before
                // the death (the death's own kill event must not count as prior info).
                // Never revealed → dark since 0:00.
                long lastReveal = -1;
                foreach (var r in enemyReveals)
                {
                    if (r >= at.TMs) break;
                    lastReveal = r;
                }
                var darkS = (int)((at.TMs - (lastReveal < 0 ? 0 : lastReveal)) / 1000);
                node["enemy_jg_dark_s"] = darkS;

                var onKill = at.KillerId == enemyId || at.AssistIds.Contains(enemyId);
                if (onKill && darkS >= FogDarkSeconds)
                    node["fog_death"] = true;
            }

            death.Details = node.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? JunglerDistanceAt(int? junglerId, TimelineDeath at, Dictionary<int, List<Sample>> samples)
    {
        if (junglerId is not { } id || !samples.TryGetValue(id, out var list)) return null;
        if (PositionAt(list, at.TMs) is not { } pos) return null;
        return (int)Math.Round(Distance((at.X, at.Y), pos));
    }

    // Piecewise-linear position at tMs from the sorted samples; nearest-sample
    // fallback at the edges within MaxSampleGapMs, null beyond that. Linear
    // interpolation across the jungler's own death/respawn is a known, bounded
    // approximation — the distances are god-view review anchors, not measurements.
    private static (double X, double Y)? PositionAt(List<Sample> sorted, long tMs)
    {
        if (sorted.Count == 0) return null;

        Sample? before = null, after = null;
        foreach (var s in sorted)
        {
            if (s.TMs <= tMs) before = s;
            else { after = s; break; }
        }
        if (before is { } b && after is { } a)
        {
            if (a.TMs == b.TMs) return (b.X, b.Y);
            var f = (double)(tMs - b.TMs) / (a.TMs - b.TMs);
            return (b.X + (a.X - b.X) * f, b.Y + (a.Y - b.Y) * f);
        }
        if (before is { } last && tMs - last.TMs <= MaxSampleGapMs) return (last.X, last.Y);
        if (after is { } next && next.TMs - tMs <= MaxSampleGapMs) return (next.X, next.Y);
        return null;
    }

    // ── small helpers ───────────────────────────────────────────────────────

    private static void AddSample(Dictionary<int, List<Sample>> samples, int pid, Sample s)
    {
        if (!samples.TryGetValue(pid, out var list)) { list = []; samples[pid] = list; }
        list.Add(s);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<int>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number) result.Add(item.GetInt32());
        return result;
    }

    private static string Str(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static long Long(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;
}
