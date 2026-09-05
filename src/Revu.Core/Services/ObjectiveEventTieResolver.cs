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
///   2. TEAMFIGHT membership — when an objective tracks a fight token (TEAMFIGHT,
///      one of the numbers verdicts, or ABSENT_TEAMFIGHT), every combat event
///      inside that fight ties to it. Fights come from <see cref="TeamfightClustering"/>:
///      stored post-game TEAMFIGHT rows with numbers, or the synthetic own-event
///      cluster (&ge;3 combat events within 14s, t&gt;0) before the pass has run.
/// </para>
///
/// Stateless and DB-free: callers fetch the active ties once
/// (<c>IObjectivesRepository.GetActiveObjectiveEventTokensAsync</c>) and pass them
/// in, so this stays unit-testable without a database.
/// </summary>
public sealed class ObjectiveEventTieResolver
{
    // token (UPPER) → ordered list of active objectives tracking it. First entry is
    // the back-compat priority-lane winner (query order, de-duped per objective).
    private readonly Dictionary<string, List<ObjectiveTie>> _tokenMap;

    // The objectives that track the generic TEAMFIGHT token (empty when none do), the
    // ones tracking ABSENT_TEAMFIGHT, and the ones tracking each numbers verdict.
    private readonly IReadOnlyList<ObjectiveTie> _teamfightObjectives;
    private readonly IReadOnlyList<ObjectiveTie> _absentObjectives;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ObjectiveTie>> _verdictObjectives;

    private ObjectiveEventTieResolver(Dictionary<string, List<ObjectiveTie>> tokenMap)
    {
        _tokenMap = tokenMap;
        _teamfightObjectives = Ties(GameEvent.TrackableTokens.TeamfightToken);
        _absentObjectives = Ties(GameEvent.TrackableTokens.AbsentTeamfightToken);
        _verdictObjectives = new Dictionary<string, IReadOnlyList<ObjectiveTie>>(StringComparer.Ordinal)
        {
            [TeamfightClustering.VerdictDown] = Ties(GameEvent.TrackableTokens.OutnumberedTeamfightToken),
            [TeamfightClustering.VerdictEven] = Ties(GameEvent.TrackableTokens.EvenTeamfightToken),
            [TeamfightClustering.VerdictUp] = Ties(GameEvent.TrackableTokens.NumbersUpTeamfightToken),
        };

        IReadOnlyList<ObjectiveTie> Ties(string token) =>
            tokenMap.TryGetValue(token, out var list) ? list : Array.Empty<ObjectiveTie>();
    }

    // True when any active objective tracks any fight token — the only case the
    // cluster pass has work to do.
    private bool TracksAnyFight =>
        _teamfightObjectives.Count > 0 || _absentObjectives.Count > 0
        || _verdictObjectives.Values.Any(v => v.Count > 0);

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

        return new ObjectiveEventTieResolver(map);
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
    /// The objectives an event ties to by its OWN TOKEN (raw type / per-spell / trade /
    /// jungle-gank) — NOT by teamfight-cluster membership. The auto-clipper uses this to
    /// decide which combat events deserve an individual clip: a kill/death/assist that an
    /// objective tracks only because it fell inside a teamfight is covered by the one
    /// per-fight clip, so it must NOT also be clipped on its own. De-duped per objective.
    /// </summary>
    public IReadOnlyList<ObjectiveTie> TokenTiesForEvent(GameEvent e)
    {
        var matches = new List<ObjectiveTie>();
        foreach (var token in EventTokens(e))
            if (_tokenMap.TryGetValue(token, out var tokenObjs))
                foreach (var t in tokenObjs)
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
        if (type == GameEvent.EventTypes.Death)
        {
            // A death can carry derived attributes stamped by post-game analysis: a
            // jungle gank (Details.jungle_gank) and/or a fog death (Details.fog_death
            // from the map-state backfill). Each attribute adds its specific token,
            // most-specific first, and the plain DEATH token always matches last.
            var isGank = ReadDetailsBool(e, "jungle_gank");
            var isFog = ReadDetailsBool(e, "fog_death");
            if (isGank || isFog)
            {
                var tokens = new List<string>(3);
                if (isGank) tokens.Add(GameEvent.TrackableTokens.JungleGankToken);
                if (isFog) tokens.Add(GameEvent.TrackableTokens.FogDeathToken);
                tokens.Add(GameEvent.EventTypes.Death);
                return tokens;
            }
            return [GameEvent.EventTypes.Death];
        }
        if (type == GameEvent.EventTypes.JungleProximity)
        {
            // Who-specific token first (priority-lane winner), generic JUNGLE_PROXIMITY
            // second — the same shape as the trade family. Unknown / missing who still
            // matches the generic token.
            return ReadDetailsString(e, "who").Trim().ToUpperInvariant() switch
            {
                "ENEMY" => [GameEvent.TrackableTokens.EnemyJungleProximityToken, GameEvent.TrackableTokens.JungleProximityToken],
                "ALLY"  => [GameEvent.TrackableTokens.AllyJungleProximityToken, GameEvent.TrackableTokens.JungleProximityToken],
                _       => [GameEvent.TrackableTokens.JungleProximityToken],
            };
        }
        if (type == GameEvent.TrackableTokens.TeamfightToken)
        {
            // A stored post-game fight row. Verdict-specific token first (priority-lane
            // winner), generic TEAMFIGHT second — but ONLY for a fight the player was
            // in. A fight that happened without the player matches ABSENT_TEAMFIGHT
            // alone; a row with no readable self state ties to nothing (never guess).
            var self = ReadDetailsString(e, "self").Trim().ToLowerInvariant();
            if (self == TeamfightClustering.SelfAway) return [GameEvent.TrackableTokens.AbsentTeamfightToken];
            if (self != TeamfightClustering.SelfIn) return [];
            var verdictToken = VerdictToken(ReadDetailsString(e, "verdict"));
            return verdictToken is null
                ? [GameEvent.TrackableTokens.TeamfightToken]
                : [verdictToken, GameEvent.TrackableTokens.TeamfightToken];
        }
        return type.Length > 0 ? [type] : [];
    }

    /// <summary>The numbers-family token for a stored fight's verdict, null when unknown.</summary>
    public static string? VerdictToken(string? verdict) => (verdict ?? "").Trim().ToLowerInvariant() switch
    {
        TeamfightClustering.VerdictDown => GameEvent.TrackableTokens.OutnumberedTeamfightToken,
        TeamfightClustering.VerdictEven => GameEvent.TrackableTokens.EvenTeamfightToken,
        TeamfightClustering.VerdictUp => GameEvent.TrackableTokens.NumbersUpTeamfightToken,
        _ => null,
    };

    /// <summary>
    /// The teamfights in a game that some active objective tracks, each tagged with the
    /// objectives it ties to. Fights are the shared <see cref="TeamfightClustering.Resolve"/>
    /// set (stored post-game rows with numbers, else the synthetic own-event clusters —
    /// the same definition the client timeline band uses). A fight the player was in
    /// ties to the TEAMFIGHT trackers plus the trackers of its numbers verdict; a fight
    /// without the player ties to the ABSENT_TEAMFIGHT trackers only. Fights nobody
    /// tracks are omitted; empty when no objective tracks any fight token. Used by the
    /// auto-clipper to make ONE clip per fight instead of one per kill/death/assist.
    /// </summary>
    public IReadOnlyList<TeamfightCluster> ResolveTeamfightClusters(IReadOnlyList<GameEvent> events)
    {
        var result = new List<TeamfightCluster>();
        if (!TracksAnyFight) return result;

        foreach (var span in TeamfightClustering.Resolve(events))
        {
            var objectives = ObjectivesFor(span);
            if (objectives.Count == 0) continue;

            // The stored row's own id leads the member list so every consumer that keys
            // coverage on MemberEventIds treats the row as part of its fight.
            var memberIds = new List<int>(span.Members.Count + 1);
            if (span.Stored is { } stored) memberIds.Add(stored.Id);
            memberIds.AddRange(span.Members.Select(m => m.Id));

            result.Add(new TeamfightCluster(
                StartS: span.StartS,
                EndS: span.EndS,
                MemberEventIds: memberIds,
                Objectives: objectives,
                StoredEventId: span.Stored?.Id,
                Self: span.Stored is null ? TeamfightClustering.SelfIn : span.Self,
                Numbers: span.Numbers,
                Verdict: span.Verdict,
                FightStartS: span.FightStartS,
                FightEndS: span.FightEndS));
        }
        return result;
    }

    // Objectives a fight ties to: TEAMFIGHT trackers first (the back-compat priority
    // lane), then the trackers of the stored verdict; away fights → ABSENT trackers.
    private IReadOnlyList<ObjectiveTie> ObjectivesFor(TeamfightSpan span)
    {
        var list = new List<ObjectiveTie>();
        void AddAll(IReadOnlyList<ObjectiveTie> ties)
        {
            foreach (var t in ties)
                if (!list.Any(x => x.ObjectiveId == t.ObjectiveId)) list.Add(t);
        }

        if (span.Stored is not null)
        {
            if (span.Self == TeamfightClustering.SelfAway) { AddAll(_absentObjectives); return list; }
            if (span.Self != TeamfightClustering.SelfIn) return list; // unreadable self state: never guess
        }
        AddAll(_teamfightObjectives);
        if (span.Stored is not null && _verdictObjectives.TryGetValue(span.Verdict, out var byVerdict))
            AddAll(byVerdict);
        return list;
    }

    // If any active objective tracks TEAMFIGHT, tie every cluster member to those
    // objectives. eventId → tied objectives. Empty when no objective tracks teamfights.
    // Built on the shared cluster computation so the timeline ties and the auto-clip
    // cluster windows can never drift.
    private IReadOnlyDictionary<int, IReadOnlyList<ObjectiveTie>> ResolveTeamfightTies(
        IReadOnlyList<GameEvent> events)
    {
        var ties = new Dictionary<int, IReadOnlyList<ObjectiveTie>>();
        foreach (var c in ResolveTeamfightClusters(events))
            foreach (var id in c.MemberEventIds)
                ties[id] = c.Objectives;
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

/// <summary>
/// A detected teamfight: the game-time span of its combat-event cluster (first→last),
/// the ids of the member events (the stored TEAMFIGHT row's own id first, when there is
/// one), and the fight-tracking objectives it ties to. The auto-clipper makes one clip
/// per cluster spanning <see cref="StartS"/>→<see cref="EndS"/>. <see cref="Numbers"/> /
/// <see cref="Verdict"/> / <see cref="Self"/> come from the stored row ("" / "in" for a
/// synthetic cluster); <see cref="FightStartS"/>→<see cref="FightEndS"/> is the fight's
/// own window (the band), null when it is just the span.
/// </summary>
public sealed record TeamfightCluster(
    int StartS,
    int EndS,
    IReadOnlyList<int> MemberEventIds,
    IReadOnlyList<ObjectiveTie> Objectives,
    int? StoredEventId = null,
    string Self = TeamfightClustering.SelfIn,
    string Numbers = "",
    string Verdict = "",
    int? FightStartS = null,
    int? FightEndS = null);
