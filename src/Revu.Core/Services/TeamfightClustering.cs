#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// One fight on a game's timeline, as every read path sees it: the span the clip /
/// anchor / band uses, the player's combat events inside it, and (when the post-game
/// pass produced one) the stored TEAMFIGHT row carrying the fight's numbers.
/// <para>
/// <see cref="StartS"/> is anchored on the EARLIEST MEMBER combat event when the fight
/// has members, so a stored fight keys its auto-clip and pattern anchor exactly where
/// the synthetic cluster keyed them before the backfill ran (no duplicate clips or
/// anchors across the transition). <see cref="FightStartS"/>/<see cref="FightEndS"/>
/// are the fight's own window (the band); for synthetic clusters they equal the span.
/// </para>
/// </summary>
public sealed record TeamfightSpan(
    int StartS,
    int EndS,
    int FightStartS,
    int FightEndS,
    IReadOnlyList<GameEvent> Members,
    GameEvent? Stored,
    string Self,
    string Numbers,
    string Verdict)
{
    /// <summary>The player took part (credited on a kill) — or the fight is a synthetic
    /// cluster of the player's own events, which implies the same.</summary>
    public bool IsOwn => Stored is null || Self == TeamfightClustering.SelfIn;
}

/// <summary>
/// THE single definition of "a teamfight" for every consumer (tie resolver, auto-clipper,
/// pattern materializer, VOD snapshot). Two sources, merged by <see cref="Resolve"/>:
/// <list type="number">
///   <item>STORED rows — <c>TEAMFIGHT</c> game_events written post-game by the map-state
///   pass from the Match-V5 timeline (every kill on the map, numbers per side).</item>
///   <item>SYNTHETIC clusters — today's rule over the player's own kill-feed events
///   (≥3 combat events chained within 14 s, t &gt; 0), kept as the fallback for games the
///   pass has not reached and as a safety net for a fight the pass could not pair.</item>
/// </list>
/// Pure and DB-free.
/// </summary>
public static class TeamfightClustering
{
    public const string SelfIn = "in";
    public const string SelfAway = "away";

    public const string VerdictUp = "up";
    public const string VerdictEven = "even";
    public const string VerdictDown = "down";

    /// <summary>Slack (seconds) when assigning the player's live-clock combat events to a
    /// stored fight's translated window, and when deciding whether a synthetic cluster
    /// overlaps a stored fight. One gap: an event that would have chained into the
    /// cluster belongs to it.</summary>
    public const int MemberPadSeconds = PatternConstants.TeamfightGapSeconds;

    public static bool IsCombat(string? eventType) =>
        (eventType ?? "").ToUpperInvariant() is "KILL" or "DEATH" or "ASSIST" or "MULTI_KILL" or "FIRST_BLOOD";

    public static bool IsStoredTeamfight(GameEvent e) =>
        string.Equals(e.EventType, GameEvent.TrackableTokens.TeamfightToken, StringComparison.OrdinalIgnoreCase);

    public static bool HasStoredFights(IEnumerable<GameEvent> events) => events.Any(IsStoredTeamfight);

    /// <summary>The fight's own window from a stored row's Details (start_s/end_s), or
    /// the row's second for anything malformed. Type-gated: only a TEAMFIGHT row is read
    /// (other row kinds also carry start_s/end_s and must never become fights).</summary>
    public static (int StartS, int EndS) ReadSpan(GameEvent e)
    {
        if (!IsStoredTeamfight(e)) return (e.GameTimeS, e.GameTimeS);
        var start = ReadInt(e, "start_s") ?? e.GameTimeS;
        var end = ReadInt(e, "end_s") ?? e.GameTimeS;
        if (end < start) end = start;
        return (start, end);
    }

    public static string ReadSelf(GameEvent e) => ReadString(e, "self").ToLowerInvariant();
    public static string ReadNumbers(GameEvent e) => ReadString(e, "numbers");
    public static string ReadVerdict(GameEvent e) => ReadString(e, "verdict").ToLowerInvariant();

    /// <summary>
    /// Today's synthetic rule, unchanged: the player's combat events (t &gt; 0) chained
    /// within <see cref="PatternConstants.TeamfightGapSeconds"/>, at least
    /// <see cref="PatternConstants.TeamfightMinEvents"/> of them. Members are the events.
    /// </summary>
    public static IReadOnlyList<TeamfightSpan> SyntheticClusters(IReadOnlyList<GameEvent> events)
    {
        var result = new List<TeamfightSpan>();
        var combat = events.Where(e => IsCombat(e.EventType) && e.GameTimeS > 0)
            .OrderBy(e => e.GameTimeS).ToList();
        var cluster = new List<GameEvent>();
        void Flush()
        {
            if (cluster.Count >= PatternConstants.TeamfightMinEvents)
            {
                var start = cluster[0].GameTimeS;
                var end = cluster[^1].GameTimeS;
                result.Add(new TeamfightSpan(start, end, start, end, cluster.ToList(), null, "", "", ""));
            }
            cluster = new List<GameEvent>();
        }
        foreach (var e in combat)
        {
            if (cluster.Count == 0 || e.GameTimeS - cluster[^1].GameTimeS <= PatternConstants.TeamfightGapSeconds)
                cluster.Add(e);
            else { Flush(); cluster.Add(e); }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Every fight in the game: one span per stored TEAMFIGHT row PLUS every synthetic
    /// cluster that overlaps no stored fight the player was in. Sorted by start. A game
    /// the pass has not processed yields exactly today's clusters.
    /// <para>Members: the player's combat events inside an "in" fight's padded window
    /// (each event to the nearest fight only). A fight the player was NOT in never
    /// takes members — it ties, clips and anchors as the row alone, so a stray own event
    /// near it keeps its own token ties. The span starts at the first member (where the
    /// player's involvement starts, and where the clip/anchor keys have always been).</para>
    /// </summary>
    public static IReadOnlyList<TeamfightSpan> Resolve(IReadOnlyList<GameEvent> events)
    {
        var stored = events.Where(IsStoredTeamfight)
            .Select(e => (Row: e, Span: ReadSpan(e), Own: ReadSelf(e) == SelfIn))
            .OrderBy(s => s.Span.StartS).ThenBy(s => s.Row.Id)
            .ToList();
        if (stored.Count == 0) return SyntheticClusters(events);

        var combat = events.Where(e => IsCombat(e.EventType) && e.GameTimeS > 0)
            .OrderBy(e => e.GameTimeS).ToList();
        var membersByRow = stored.ToDictionary(s => s.Row, _ => new List<GameEvent>());
        foreach (var e in combat)
        {
            GameEvent? bestRow = null;
            var bestDistance = int.MaxValue;
            foreach (var s in stored)
            {
                if (!s.Own) continue;
                var distance = e.GameTimeS < s.Span.StartS ? s.Span.StartS - e.GameTimeS
                    : e.GameTimeS > s.Span.EndS ? e.GameTimeS - s.Span.EndS
                    : 0;
                if (distance <= MemberPadSeconds && distance < bestDistance)
                {
                    bestRow = s.Row;
                    bestDistance = distance;
                }
            }
            if (bestRow is not null) membersByRow[bestRow].Add(e);
        }

        var result = new List<TeamfightSpan>();
        foreach (var s in stored)
        {
            var members = membersByRow[s.Row];
            var start = members.Count > 0 ? members[0].GameTimeS : s.Span.StartS;
            var end = members.Count > 0 ? Math.Max(members[^1].GameTimeS, s.Span.EndS) : s.Span.EndS;
            if (end < start) end = start;
            result.Add(new TeamfightSpan(
                start, end, s.Span.StartS, s.Span.EndS, members, s.Row,
                ReadSelf(s.Row), ReadNumbers(s.Row), ReadVerdict(s.Row)));
        }

        // Safety net: a synthetic cluster the pass could not pair with any stored fight
        // the player was in (clock skew beyond tolerance, a fight below the timeline's
        // kill floor) keeps rendering exactly as it does today instead of vanishing.
        foreach (var synthetic in SyntheticClusters(events))
        {
            var overlaps = stored.Any(s => s.Own
                && synthetic.StartS <= s.Span.EndS + MemberPadSeconds
                && synthetic.EndS >= s.Span.StartS - MemberPadSeconds);
            if (!overlaps) result.Add(synthetic);
        }

        return result.OrderBy(r => r.StartS).ThenBy(r => r.Stored?.Id ?? int.MaxValue).ToList();
    }

    /// <summary>
    /// The dedupe key suffix for a fight: the span start, plus "-end" only when an
    /// earlier span in the same game already claimed that start (two fights on opposite
    /// sides of the map can begin in the same second). Keeps every existing key stable
    /// while making colliding ones distinct. <paramref name="claimed"/> is per game.
    /// </summary>
    public static string KeyAnchor(TeamfightSpan span, ISet<string> claimed)
    {
        var anchor = span.StartS.ToString();
        if (!claimed.Add(anchor)) anchor = $"{span.StartS}-{span.EndS}";
        return anchor;
    }

    private static int? ReadInt(GameEvent e, string property)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Details);
            return doc.RootElement.TryGetProperty(property, out var v) && v.TryGetInt32(out var i) ? i : null;
        }
        catch { return null; }
    }

    private static string ReadString(GameEvent e, string property)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return "";
        try
        {
            using var doc = JsonDocument.Parse(e.Details);
            return doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
}
