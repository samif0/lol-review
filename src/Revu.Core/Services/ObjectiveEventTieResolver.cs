#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Shared, PURE logic for deciding which active learning objectives a game event
/// is tied to. This is the single source of truth used by BOTH the VOD snapshot
/// timeline (which lights tied events up in the priority lane) and the on-demand
/// auto-clipper (which clips exactly the events the timeline highlights). Lifting
/// it out of <c>VodSnapshotBuilder</c> guarantees the two can never drift.
///
/// <para>
/// Two tie sources, matching the timeline:
///   1. TOKEN match — the event's own trackable token (raw type, or SPELL_&lt;name&gt;
///      parsed from Details.spell for summoner casts) is tracked by an objective.
///   2. TEAMFIGHT membership — when an objective tracks TEAMFIGHT, every combat
///      event inside a cluster (&ge;3 combat events within 14s, t&gt;0) ties to it,
///      even though no single row IS a teamfight.
/// </para>
///
/// Stateless and DB-free: callers fetch the active ties once
/// (<c>IObjectivesRepository.GetActiveObjectiveEventTokensAsync</c>) and pass them
/// in, so this stays unit-testable without a database.
/// </summary>
public sealed class ObjectiveEventTieResolver
{
    private const int TeamfightGapSeconds = 14;
    private const int TeamfightMinEvents = 3;

    // token (UPPER) → ordered list of active objectives tracking it. First entry is
    // the back-compat priority-lane winner (query order, de-duped per objective).
    private readonly Dictionary<string, List<ObjectiveTie>> _tokenMap;

    // The objectives that track TEAMFIGHT (empty when none do), in the same order.
    private readonly IReadOnlyList<ObjectiveTie> _teamfightObjectives;

    private ObjectiveEventTieResolver(
        Dictionary<string, List<ObjectiveTie>> tokenMap,
        IReadOnlyList<ObjectiveTie> teamfightObjectives)
    {
        _tokenMap = tokenMap;
        _teamfightObjectives = teamfightObjectives;
    }

    /// <summary>
    /// Build a resolver from the active (token, objectiveId, title) ties. Color is
    /// derived from the token catalog so callers don't have to supply it.
    /// </summary>
    public static ObjectiveEventTieResolver FromTies(
        IEnumerable<(string Token, long ObjectiveId, string Title)> ties)
    {
        var map = new Dictionary<string, List<ObjectiveTie>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, objId, title) in ties)
        {
            var key = (token ?? "").Trim().ToUpperInvariant();
            if (key.Length == 0) continue;
            if (!map.TryGetValue(key, out var list)) { list = new List<ObjectiveTie>(); map[key] = list; }
            if (list.Any(t => t.ObjectiveId == objId)) continue; // de-dupe per objective
            var color = GameEvent.TrackableTokens.ColorOf(key);
            list.Add(new ObjectiveTie(objId, title ?? "", color));
        }

        map.TryGetValue(GameEvent.TrackableTokens.TeamfightToken, out var tfList);
        return new ObjectiveEventTieResolver(
            map,
            (IReadOnlyList<ObjectiveTie>?)tfList ?? Array.Empty<ObjectiveTie>());
    }

    /// <summary>True if no active objective ties to any event token (UI can skip work).</summary>
    public bool IsEmpty => _tokenMap.Count == 0;

    /// <summary>
    /// For a whole game's events, return each event's tied active objectives (empty
    /// list when untied). Resolves teamfight membership across the set once. The
    /// FIRST entry per event is the priority-lane winner. Mirrors what the timeline
    /// renders, so an auto-clipper iterating these clips exactly the loud markers.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<ObjectiveTie>> ResolveForGame(
        IReadOnlyList<GameEvent> events)
    {
        var teamfightTies = ResolveTeamfightTies(events);
        var result = new Dictionary<int, IReadOnlyList<ObjectiveTie>>();
        foreach (var e in events)
            result[e.Id] = TiesForEvent(e, teamfightTies);
        return result;
    }

    /// <summary>
    /// The tied objectives for a single event, given the pre-computed teamfight ties
    /// (eventId → objectives). Token match first, then teamfight membership (de-duped).
    /// </summary>
    public IReadOnlyList<ObjectiveTie> TiesForEvent(
        GameEvent e,
        IReadOnlyDictionary<int, IReadOnlyList<ObjectiveTie>> teamfightTies)
    {
        var matches = new List<ObjectiveTie>();
        // An event can match more than one trackable token (a trade matches both the
        // generic TRADE and its kind-specific token); add every tracking objective,
        // de-duped per objective so a card double-tracking the same event lists once.
        foreach (var token in EventTokens(e))
        {
            if (_tokenMap.TryGetValue(token, out var tokenObjs))
                foreach (var t in tokenObjs)
                    if (!matches.Any(m => m.ObjectiveId == t.ObjectiveId)) matches.Add(t);
        }
        if (teamfightTies.TryGetValue(e.Id, out var tfObjs))
            foreach (var t in tfObjs)
                if (!matches.Any(m => m.ObjectiveId == t.ObjectiveId)) matches.Add(t);
        return matches;
    }

    /// <summary>
    /// The PRIMARY trackable token for an event (the priority-lane winner): per-spell
    /// (SPELL_FLASH/SPELL_SMITE/…) for summoner casts, the kind-specific trade token
    /// for a TRADE, otherwise the raw event type (UPPER). Null only for an empty type.
    /// Use <see cref="EventTokens"/> when you need every token an event matches.
    /// </summary>
    public static string? EventToken(GameEvent e) => EventTokens(e).FirstOrDefault();

    /// <summary>
    /// EVERY trackable token an event matches, most-specific first. Most events match
    /// exactly one token (their raw type, or per-spell SPELL_* for summoner casts).
    /// A TRADE matches two — its kind-specific token (SHORT_TRADE / EXTENDED_TRADE,
    /// from Details.kind) AND the generic TRADE — so an objective can track all trades
    /// or just one severity. Empty only for an empty event type.
    /// </summary>
    public static IReadOnlyList<string> EventTokens(GameEvent e)
    {
        var type = (e.EventType ?? "").ToUpperInvariant();
        if (type is "FLASH" or "SUMMONER_SPELL")
        {
            var spell = ReadSpellName(e);
            if (!string.IsNullOrWhiteSpace(spell))
                return [GameEvent.TrackableTokens.SpellPrefix + spell.Trim().ToUpperInvariant()];
            // Legacy rows with no Details.spell: FLASH still maps to its spell token.
            return type == "FLASH" ? ["SPELL_FLASH"] : [];
        }
        if (type == GameEvent.EventTypes.Trade)
        {
            var kind = ReadDetailsString(e, "kind").Trim().ToUpperInvariant();
            // Kind-specific token first (priority-lane winner), generic TRADE second.
            // An unknown / missing kind still matches the generic TRADE.
            return kind switch
            {
                "SHORT"    => [GameEvent.TrackableTokens.ShortTradeToken, GameEvent.TrackableTokens.TradeToken],
                "EXTENDED" => [GameEvent.TrackableTokens.ExtendedTradeToken, GameEvent.TrackableTokens.TradeToken],
                _          => [GameEvent.TrackableTokens.TradeToken],
            };
        }
        if (type == GameEvent.EventTypes.Death && ReadDetailsBool(e, "jungle_gank"))
        {
            // A jungle-ganked death matches the specific JUNGLE_GANK token (priority-lane
            // winner) AND the plain DEATH token, so an objective tracking either fires.
            return [GameEvent.TrackableTokens.JungleGankToken, GameEvent.EventTypes.Death];
        }
        return type.Length > 0 ? [type] : [];
    }

    // If any active objective tracks TEAMFIGHT, cluster the combat events (≥3 combat
    // events within 14s, t>0 — same definition the client timeline uses) and tie every
    // member to those objectives. eventId → tied objectives. Empty when no objective
    // tracks teamfights.
    private IReadOnlyDictionary<int, IReadOnlyList<ObjectiveTie>> ResolveTeamfightTies(
        IReadOnlyList<GameEvent> events)
    {
        var ties = new Dictionary<int, IReadOnlyList<ObjectiveTie>>();
        if (_teamfightObjectives.Count == 0) return ties;

        static bool IsCombat(string? t) =>
            (t ?? "").ToUpperInvariant() is "KILL" or "DEATH" or "ASSIST" or "MULTI_KILL" or "FIRST_BLOOD";

        // t>0 mirrors the client teamfightZones() filter: a t=0 combat event
        // (unresolved EventTime) is dropped client-side, so drop it here too to keep
        // the ties aligned with the visible band.
        var combat = events.Where(e => IsCombat(e.EventType) && e.GameTimeS > 0)
            .OrderBy(e => e.GameTimeS).ToList();
        var cluster = new List<GameEvent>();
        void Flush()
        {
            if (cluster.Count >= TeamfightMinEvents)
                foreach (var m in cluster) ties[m.Id] = _teamfightObjectives;
            cluster = new List<GameEvent>();
        }
        foreach (var e in combat)
        {
            if (cluster.Count == 0 || e.GameTimeS - cluster[^1].GameTimeS <= TeamfightGapSeconds)
                cluster.Add(e);
            else { Flush(); cluster.Add(e); }
        }
        Flush();
        return ties;
    }

    // The specific summoner-spell name from Details.spell ("Flash"|"Ignite"|…), "" if absent.
    private static string ReadSpellName(GameEvent e) => ReadDetailsString(e, "spell");

    // A string property out of an event's Details JSON, "" if absent / not a string.
    private static string ReadDetailsString(GameEvent e, string property)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            var root = doc.RootElement;
            if (root.TryGetProperty(property, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString() ?? "";
            return "";
        }
        catch { return ""; }
    }

    // A bool property out of an event's Details JSON, false if absent / not a bool.
    private static bool ReadDetailsBool(GameEvent e, string property)
    {
        if (string.IsNullOrWhiteSpace(e.Details) || e.Details == "{}") return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.Details);
            return doc.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { return false; }
    }
}

/// <summary>One active objective an event ties to: its id, title, and lane color.</summary>
public readonly record struct ObjectiveTie(long ObjectiveId, string Title, string Color);
