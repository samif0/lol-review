#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using Revu.Core.Constants;
using Revu.Core.Models;
using static Revu.Core.Services.TeamfightRules;

namespace Revu.Core.Services;

/// <summary>Result of one game's teamfight pass: TEAMFIGHT rows to insert (GameId
/// left 0) and the stored DEATH rows whose Details changed (fight stamps added or
/// stale ones removed).</summary>
public sealed record TeamfightAnalysis(
    IReadOnlyList<GameEvent> Fights,
    IReadOnlyList<GameEvent> StampedDeaths)
{
    public static readonly TeamfightAnalysis Empty = new([], []);
}

/// <summary>
/// v3.8: teamfights with NUMBERS, derived post-game from the Match-V5 timeline. The
/// first "filter" of a fight for the join-or-avoid decision: how many were on each side.
///
/// <para>A fight is a cluster of champion kills close in TIME (≤14 s apart) and SPACE
/// (within <see cref="PatternConstants.TeamfightLinkRadiusUnits"/> of the cluster) with
/// at least <see cref="PatternConstants.TeamfightMinKills"/> kills, or a smaller cluster
/// the player fought in that lines up with today's own-event cluster rule (so nothing
/// that is a fight today stops being one). Lone executes (no champion involved) never
/// count.</para>
///
/// <para>Numbers are CREDITED ONLY: the kill feed's killer / victim / assisters plus
/// the champions that exchanged damage with each victim. No positions are read, so a
/// count never asserts what the player could see. For a fight the player was in, the
/// headline number is taken at the player's COMMITMENT instant (the first kill they are
/// credited on): everyone credited from 10 s before to 3 s after it who was alive then.
/// The whole-fight count is kept as "became" so a late arrival reads as what it is.
/// Fights the player was not in are stored too (self = "away"): they are the evidence
/// for the avoid half of the decision.</para>
///
/// Pure and DB-free; the caller persists the rows and stamps.
/// </summary>
public static class TeamfightAnalyzer
{
    public const int Version = 1;

    /// <summary>Credit window around the player's entry kill (ms): kills up to 10 s before
    /// (the assist window of the kill feed) and 3 s after (the same burst) describe who was
    /// there when the player committed; later kills are arrivals, counted in "became".</summary>
    private const long EntryLookbackMs = 10_000;
    private const long EntryLookaheadMs = 3_000;

    /// <summary>A death this close before the entry instant is part of the same burst
    /// (double kill), not an earlier casualty — the victim still counts as present.</summary>
    private const long EntryDeathGraceMs = 3_000;

    private const int ClockMatchToleranceS = 25;
    private const int ClockOffsetAgreementS = 2;

    private sealed record Participant(int Pid, int TeamId, string Champion, string Role);
    private sealed record Roster(int SelfId, int SelfTeam, IReadOnlyDictionary<int, Participant> ById)
    {
        public IReadOnlySet<int> Ids => ById.Keys.ToHashSet();
    }

    private sealed class Cluster
    {
        public readonly List<Kill> Kills = [];
        public long LastMs => Kills[^1].TMs;
        public long FirstMs => Kills[0].TMs;
    }

    public static TeamfightAnalysis Analyze(
        JsonElement match, JsonElement timeline, string puuid, IReadOnlyList<GameEvent> storedEvents)
    {
        if (!IsClassicRift(match)) return TeamfightAnalysis.Empty;
        var roster = ResolveRoster(match, puuid);
        if (roster is null) return TeamfightAnalysis.Empty;
        if (!timeline.TryGetProperty("info", out var info)
            || !info.TryGetProperty("frames", out var frames)
            || frames.ValueKind != JsonValueKind.Array)
            return TeamfightAnalysis.Empty;

        var kills = new List<Kill>();
        var levelUps = new Dictionary<int, List<(long TMs, int Level)>>();
        foreach (var frame in frames.EnumerateArray())
        {
            var frameTs = TimelineJson.Long(frame, "timestamp");
            CollectFrameLevels(frame, frameTs, roster, levelUps);
            if (!frame.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) continue;
            foreach (var ev in events.EnumerateArray())
            {
                if (ParseKill(ev) is { } kill) { kills.Add(kill); continue; }
                if (TimelineJson.Str(ev, "type") == "LEVEL_UP")
                {
                    var pid = TimelineJson.Int(ev, "participantId");
                    if (roster.ById.ContainsKey(pid))
                        Add(levelUps, pid, (TimelineJson.Long(ev, "timestamp"), TimelineJson.Int(ev, "level")));
                }
            }
        }
        kills.Sort(static (a, b) => a.TMs.CompareTo(b.TMs));
        foreach (var list in levelUps.Values) list.Sort(static (a, b) => a.TMs.CompareTo(b.TMs));

        var (offsetS, offsetN) = ClockOffset(storedEvents, kills, roster);
        var synthetic = TeamfightClustering.SyntheticClusters(storedEvents);
        var fights = new List<(GameEvent Row, Cluster Cluster, bool Own)>();
        foreach (var cluster in ClusterKills(kills.Where(k => !k.IsLoneExecute).ToList()))
        {
            var selfCredited = cluster.Kills.Any(k => IsCredited(k, roster.SelfId, roster.Ids));
            var isFight = cluster.Kills.Count >= PatternConstants.TeamfightMinKills
                || (cluster.Kills.Count >= 2 && selfCredited && OverlapsSynthetic(cluster, synthetic, offsetS));
            if (!isFight) continue;
            if (BuildRow(cluster, roster, kills, levelUps, selfCredited, offsetS, offsetN) is { } row)
                fights.Add((row, cluster, selfCredited));
        }
        fights.Sort(static (a, b) => a.Row.GameTimeS.CompareTo(b.Row.GameTimeS));

        var stamped = StampDeaths(storedEvents, kills, roster, fights);
        return new TeamfightAnalysis(fights.Select(f => f.Row).ToList(), stamped);
    }

    // ── gates and roster ────────────────────────────────────────────────────

    // Summoner's Rift classic only: the respawn table and the 100/200 team split are
    // wrong on Howling Abyss, Arena and the rotating modes. Missing fields (older
    // payloads, test fixtures) are treated as classic.
    private static bool IsClassicRift(JsonElement match)
    {
        if (!match.TryGetProperty("info", out var info)) return true;
        if (info.TryGetProperty("mapId", out var map) && map.ValueKind == JsonValueKind.Number && map.GetInt32() != 11)
            return false;
        var mode = TimelineJson.Str(info, "gameMode");
        return mode.Length == 0 || mode == "CLASSIC";
    }

    private static Roster? ResolveRoster(JsonElement match, string puuid)
    {
        if (!match.TryGetProperty("info", out var info)
            || !info.TryGetProperty("participants", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
            return null;
        var byId = new Dictionary<int, Participant>();
        int selfId = 0, selfTeam = 0;
        foreach (var p in parts.EnumerateArray())
        {
            var pid = TimelineJson.Int(p, "participantId");
            var team = TimelineJson.Int(p, "teamId");
            if (pid <= 0 || team == 0) continue;
            byId[pid] = new Participant(pid, team, TimelineJson.Str(p, "championName"), TimelineJson.Str(p, "teamPosition"));
            if (string.Equals(TimelineJson.Str(p, "puuid"), puuid, StringComparison.OrdinalIgnoreCase))
            {
                selfId = pid;
                selfTeam = team;
            }
        }
        return selfId > 0 ? new Roster(selfId, selfTeam, byId) : null;
    }

    private static void CollectFrameLevels(
        JsonElement frame, long frameTs, Roster roster, Dictionary<int, List<(long, int)>> levels)
    {
        if (!frame.TryGetProperty("participantFrames", out var pFrames) || pFrames.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in pFrames.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var pid) || !roster.ById.ContainsKey(pid)) continue;
            var level = TimelineJson.Int(prop.Value, "level");
            if (level > 0) Add(levels, pid, (frameTs, level));
        }
    }

    private static void Add<T>(Dictionary<int, List<T>> map, int pid, T item)
    {
        if (!map.TryGetValue(pid, out var list)) { list = []; map[pid] = list; }
        list.Add(item);
    }

    // ── clock alignment ─────────────────────────────────────────────────────

    // Stored events run on the live kill-feed clock, the timeline on the server's.
    // Pair the player's own KILL / DEATH / ASSIST rows with the timeline kills that
    // credit them in the same role (nearest unclaimed within 25 s) and take the median
    // difference — trusted only when at least two pairs agree within 2 s.
    private static (int OffsetS, int Pairs) ClockOffset(IReadOnlyList<GameEvent> stored, List<Kill> kills, Roster roster)
    {
        var claimed = new bool[kills.Count];
        var diffs = new List<int>();
        foreach (var e in stored.Where(e => e.GameTimeS > 0).OrderBy(e => e.GameTimeS))
        {
            var type = (e.EventType ?? "").ToUpperInvariant();
            if (type is not ("KILL" or "DEATH" or "ASSIST")) continue;
            var best = -1;
            var bestDelta = int.MaxValue;
            for (var i = 0; i < kills.Count; i++)
            {
                if (claimed[i]) continue;
                var k = kills[i];
                var roleMatch = type switch
                {
                    "KILL" => k.KillerId == roster.SelfId,
                    "DEATH" => k.VictimId == roster.SelfId,
                    _ => k.AssistIds.Contains(roster.SelfId),
                };
                if (!roleMatch) continue;
                var delta = Math.Abs((int)(k.TMs / 1000) - e.GameTimeS);
                if (delta < bestDelta) { best = i; bestDelta = delta; }
            }
            if (best < 0 || bestDelta > ClockMatchToleranceS) continue;
            claimed[best] = true;
            diffs.Add((int)(kills[best].TMs / 1000) - e.GameTimeS);
        }
        if (diffs.Count < 2) return (0, diffs.Count);
        diffs.Sort();
        if (diffs[^1] - diffs[0] > ClockOffsetAgreementS) return (0, diffs.Count);
        return (diffs[diffs.Count / 2], diffs.Count);
    }

    private static int ToLiveSeconds(long tMs, int offsetS) => Math.Max(0, (int)(tMs / 1000) - offsetS);

    private static bool OverlapsSynthetic(Cluster cluster, IReadOnlyList<TeamfightSpan> synthetic, int offsetS)
    {
        var pad = TeamfightClustering.MemberPadSeconds * 1000L;
        foreach (var s in synthetic)
        {
            var startMs = (s.StartS + offsetS) * 1000L - pad;
            var endMs = (s.EndS + offsetS) * 1000L + pad;
            if (cluster.FirstMs <= endMs && cluster.LastMs >= startMs) return true;
        }
        return false;
    }

    // ── clustering (time AND space) ─────────────────────────────────────────

    // A kill joins an open cluster when it lands within the gap of that cluster's last
    // kill AND within the link radius of any kill already in it; among eligible clusters
    // the nearest wins. Two skirmishes on opposite sides of the map at the same second
    // therefore stay two fights. A kill without a position joins by time alone.
    private static List<Cluster> ClusterKills(List<Kill> kills)
    {
        var gapMs = PatternConstants.TeamfightGapSeconds * 1000L;
        var open = new List<Cluster>();
        var closed = new List<Cluster>();
        foreach (var kill in kills)
        {
            for (var i = open.Count - 1; i >= 0; i--)
            {
                if (kill.TMs - open[i].LastMs > gapMs) { closed.Add(open[i]); open.RemoveAt(i); }
            }
            Cluster? best = null;
            var bestDist = double.MaxValue;
            foreach (var c in open)
            {
                var dist = MinDistance(kill, c);
                if (dist <= PatternConstants.TeamfightLinkRadiusUnits && dist < bestDist) { best = c; bestDist = dist; }
            }
            if (best is null) { best = new Cluster(); open.Add(best); }
            best.Kills.Add(kill);
        }
        closed.AddRange(open);
        closed.Sort(static (a, b) => a.FirstMs.CompareTo(b.FirstMs));
        return closed;
    }

    private static double MinDistance(Kill kill, Cluster cluster)
    {
        if (!kill.HasPosition) return 0;
        var best = double.MaxValue;
        foreach (var k in cluster.Kills)
        {
            if (!k.HasPosition) return 0;
            var dx = k.X - kill.X;
            var dy = k.Y - kill.Y;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    // ── numbers and the stored row ──────────────────────────────────────────

    private static GameEvent? BuildRow(
        Cluster cluster, Roster roster, List<Kill> allKills,
        Dictionary<int, List<(long TMs, int Level)>> levels, bool own, int offsetS, int offsetN)
    {
        var ids = roster.Ids;
        var everyone = new HashSet<int>();
        foreach (var k in cluster.Kills) everyone.UnionWith(Credited(k, ids));

        var entryKill = own ? cluster.Kills.First(k => IsCredited(k, roster.SelfId, ids)) : null;
        HashSet<int> counted;
        if (entryKill is { } entry)
        {
            counted = [];
            foreach (var k in cluster.Kills)
                if (k.TMs >= entry.TMs - EntryLookbackMs && k.TMs <= entry.TMs + EntryLookaheadMs)
                    counted.UnionWith(Credited(k, ids));
            counted.RemoveWhere(pid => IsDeadAt(pid, entry.TMs, allKills, levels));
        }
        else counted = everyone;

        int Allies(IEnumerable<int> set) => set.Count(p => roster.ById[p].TeamId == roster.SelfTeam);
        int Enemies(IEnumerable<int> set) => set.Count(p => roster.ById[p].TeamId != roster.SelfTeam);

        var allies = Allies(counted);
        var enemies = Enemies(counted);
        var becameAllies = Allies(everyone);
        var becameEnemies = Enemies(everyone);
        var killsFor = cluster.Kills.Count(k => roster.ById.TryGetValue(k.VictimId, out var v) && v.TeamId != roster.SelfTeam);
        var killsAgainst = cluster.Kills.Count - killsFor;

        var startS = ToLiveSeconds(cluster.FirstMs, offsetS);
        var endS = Math.Max(startS, ToLiveSeconds(cluster.LastMs, offsetS));
        if ((int)(cluster.LastMs / 1000) - offsetS < 0) return null; // whole window before 0:00 — clock garbage

        var numbers = NumbersLabel(allies, enemies);
        var verdict = Verdict(allies, enemies);
        var became = NumbersLabel(becameAllies, becameEnemies);

        var participants = new JsonArray();
        var allyChampions = new JsonArray();
        var enemyChampions = new JsonArray();
        foreach (var pid in everyone.OrderBy(p => roster.ById[p].TeamId != roster.SelfTeam).ThenBy(p => p))
        {
            var p = roster.ById[pid];
            var ally = p.TeamId == roster.SelfTeam;
            participants.Add(new JsonObject
            {
                ["pid"] = pid,
                ["team"] = ally ? "ally" : "enemy",
                ["champion"] = p.Champion,
                ["role"] = p.Role,
                ["credited"] = true,
                ["entry"] = counted.Contains(pid),
                ["self"] = pid == roster.SelfId,
            });
            if (!counted.Contains(pid)) continue;
            (ally ? allyChampions : enemyChampions).Add(p.Champion);
        }

        var detail = own
            ? $"{allies} allies vs {enemies} enemies on the kill feed when you committed; became {became}"
            : $"{allies} allies vs {enemies} enemies on the kill feed; you were not credited in this fight";

        var details = new JsonObject
        {
            ["detected"] = true,
            ["v"] = Version,
            ["start_s"] = startS,
            ["end_s"] = endS,
            ["clock_offset_s"] = offsetS,
            ["clock_offset_n"] = offsetN,
            ["self"] = own ? TeamfightClustering.SelfIn : TeamfightClustering.SelfAway,
            ["numbers"] = numbers,
            ["allies"] = allies,
            ["enemies"] = enemies,
            ["delta"] = allies - enemies,
            ["verdict"] = verdict,
            ["became"] = became,
            ["became_allies"] = becameAllies,
            ["became_enemies"] = becameEnemies,
            ["kills"] = cluster.Kills.Count,
            ["kills_for"] = killsFor,
            ["kills_against"] = killsAgainst,
            ["outcome"] = Outcome(killsFor, killsAgainst),
            ["ally_champions"] = allyChampions,
            ["enemy_champions"] = enemyChampions,
            ["participants"] = participants,
            ["filters"] = new JsonObject
            {
                ["numbers"] = new JsonObject
                {
                    ["label"] = numbers,
                    ["favor"] = Favor(allies, enemies),
                    ["verdict"] = verdict,
                    ["detail"] = detail,
                },
            },
        };
        if (entryKill is { } ek) details["entry_s"] = ToLiveSeconds(ek.TMs, offsetS);

        return new GameEvent
        {
            EventType = GameEvent.TrackableTokens.TeamfightToken,
            GameTimeS = startS,
            Details = details.ToJsonString(),
        };
    }

    // Dead at t: a death strictly before the burst grace whose respawn has not elapsed.
    private static bool IsDeadAt(int pid, long tMs, List<Kill> allKills, Dictionary<int, List<(long TMs, int Level)>> levels)
    {
        foreach (var k in allKills)
        {
            if (k.VictimId != pid || k.TMs >= tMs - EntryDeathGraceMs) continue;
            var respawnMs = (long)(RespawnSeconds(LevelAt(pid, k.TMs, levels), k.TMs) * 1000);
            if (tMs < k.TMs + respawnMs) return true;
        }
        return false;
    }

    private static int LevelAt(int pid, long tMs, Dictionary<int, List<(long TMs, int Level)>> levels)
    {
        var level = 1;
        if (!levels.TryGetValue(pid, out var list)) return level;
        foreach (var (ts, l) in list)
        {
            if (ts > tMs) break;
            level = Math.Max(level, l);
        }
        return level;
    }

    // ── death stamps ────────────────────────────────────────────────────────

    // Strip stale fight keys from every stored DEATH first (a re-run must converge),
    // then stamp each death that pairs with a kill inside a fight the player was in.
    private static IReadOnlyList<GameEvent> StampDeaths(
        IReadOnlyList<GameEvent> stored, List<Kill> kills, Roster roster,
        List<(GameEvent Row, Cluster Cluster, bool Own)> fights)
    {
        var changed = new List<GameEvent>();
        var deaths = stored.Where(e => string.Equals(e.EventType, GameEvent.EventTypes.Death, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.GameTimeS).ToList();
        foreach (var d in deaths)
            if (RemoveFightKeys(d)) changed.Add(d);

        var selfDeaths = kills.Where(k => k.VictimId == roster.SelfId).ToList();
        var claimed = new bool[selfDeaths.Count];
        foreach (var d in deaths)
        {
            var best = -1;
            var bestDelta = int.MaxValue;
            for (var i = 0; i < selfDeaths.Count; i++)
            {
                if (claimed[i]) continue;
                var delta = Math.Abs((int)(selfDeaths[i].TMs / 1000) - d.GameTimeS);
                if (delta < bestDelta) { best = i; bestDelta = delta; }
            }
            if (best < 0 || bestDelta > ClockMatchToleranceS) continue;
            claimed[best] = true;
            var kill = selfDeaths[best];
            var fight = fights.FirstOrDefault(f => f.Own && f.Cluster.Kills.Contains(kill));
            if (fight.Row is null) continue;
            if (TryStamp(d, fight.Row) && !changed.Contains(d)) changed.Add(d);
        }
        return changed;
    }

    private static bool RemoveFightKeys(GameEvent death)
    {
        if (string.IsNullOrWhiteSpace(death.Details) || death.Details == "{}") return false;
        try
        {
            if (JsonNode.Parse(death.Details) is not JsonObject node) return false;
            var removed = node.Remove("fight_numbers") | node.Remove("fight_verdict") | node.Remove("fight_start_s");
            if (removed) death.Details = node.ToJsonString();
            return removed;
        }
        catch { return false; }
    }

    private static bool TryStamp(GameEvent death, GameEvent fight)
    {
        try
        {
            var node = string.IsNullOrWhiteSpace(death.Details) || death.Details == "{}"
                ? new JsonObject()
                : JsonNode.Parse(death.Details) as JsonObject ?? new JsonObject();
            node["fight_numbers"] = TeamfightClustering.ReadNumbers(fight);
            node["fight_verdict"] = TeamfightClustering.ReadVerdict(fight);
            node["fight_start_s"] = fight.GameTimeS;
            death.Details = node.ToJsonString();
            return true;
        }
        catch { return false; }
    }
}
