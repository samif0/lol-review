#nullable enable

using System.Globalization;
using System.Text.Json.Nodes;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>One candidate matched to a correction: its index in the candidate list, whether the
/// match was an exact key hit, and the candidate's own key (the rebase target on a fuzzy hit).</summary>
public sealed record EventMatch(int Index, bool Exact, string CandidateKey);

/// <summary>
/// Stable identity for game_events rows and the matching rules that let a correction find its
/// subject again after a re-capture regenerated every row id. Pure, DB-free: the key is derived
/// from what the detector cannot change without meaning a different event (type, anchor second,
/// one discriminating name); correctable attributes are never part of it.
/// </summary>
public static class EventIdentity
{
    public const string DetectedPrefix = "det:";
    public const string UserPrefix = "usr:";

    /// <summary>det:{TYPE}:{anchor_s}:{disc}. No collision suffix. TYPE upper-cased.</summary>
    public static string KeyFor(GameEvent e)
    {
        var type = (e.EventType ?? "").Trim().ToUpperInvariant();
        return DetectedPrefix + type + ":" + AnchorOf(e).ToString(CultureInfo.InvariantCulture) + ":" + DiscOf(type, e.Details);
    }

    /// <summary>Keys aligned with <paramref name="batch"/>. Rows sharing a base key get
    /// "#2", "#3" ... in (GameTimeS asc, batch index asc) order; the first keeps the bare key.
    /// Rows whose details carry a correction marker or source=reviewed_encounter still get
    /// a key (callers skip them); rows with an existing usr: key keep it.</summary>
    public static IReadOnlyList<string> KeyForBatch(IReadOnlyList<GameEvent> batch)
    {
        var keys = new string[batch.Count];
        var order = Enumerable.Range(0, batch.Count)
            .OrderBy(i => batch[i].GameTimeS)
            .ThenBy(i => i)
            .ToList();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in order)
        {
            var e = batch[i];
            if (IsUserKey(e.EventKey))
            {
                keys[i] = e.EventKey!;
                continue;
            }
            var baseKey = KeyFor(e);
            seen.TryGetValue(baseKey, out var n);
            n++;
            seen[baseKey] = n;
            keys[i] = n == 1 ? baseKey : baseKey + "#" + n.ToString(CultureInfo.InvariantCulture);
        }
        return keys;
    }

    public static string UserKey(string correctionId) => UserPrefix + correctionId;
    public static bool IsUserKey(string? key) => key is not null && key.StartsWith(UserPrefix, StringComparison.Ordinal);

    /// <summary>game_time_s, except TEAMFIGHT: details.start_s ?? game_time_s.</summary>
    public static int AnchorOf(GameEvent e) => AnchorOf(e.EventType, e.GameTimeS, e.Details);

    public static int AnchorOf(string eventType, int gameTimeS, string details)
    {
        if (!string.Equals(eventType, GameEvent.TrackableTokens.TeamfightToken, StringComparison.OrdinalIgnoreCase))
            return gameTimeS;
        return EventJson.ReadInt(EventJson.TryParseObject(details), "start_s") ?? gameTimeS;
    }

    /// <summary>KILL victim; DEATH killer; ASSIST "killer&gt;victim"; DRAGON dragon_type; TURRET turret;
    /// JUNGLE_PROXIMITY who; else "". Trimmed; ':' and '#' percent-escaped (%3A, %23) so keys parse.</summary>
    public static string DiscOf(string eventType, string details)
    {
        var type = (eventType ?? "").Trim().ToUpperInvariant();
        JsonObject? d;
        switch (type)
        {
            case GameEvent.EventTypes.Kill:
                d = EventJson.TryParseObject(details);
                return Escape(EventJson.ReadString(d, "victim"));
            case GameEvent.EventTypes.Death:
                d = EventJson.TryParseObject(details);
                return Escape(EventJson.ReadString(d, "killer"));
            case GameEvent.EventTypes.Assist:
                d = EventJson.TryParseObject(details);
                var killer = Escape(EventJson.ReadString(d, "killer"));
                var victim = Escape(EventJson.ReadString(d, "victim"));
                return killer.Length == 0 && victim.Length == 0 ? "" : killer + ">" + victim;
            case GameEvent.EventTypes.Dragon:
                d = EventJson.TryParseObject(details);
                return Escape(EventJson.ReadString(d, "dragon_type"));
            case GameEvent.EventTypes.Turret:
                d = EventJson.TryParseObject(details);
                return Escape(EventJson.ReadString(d, "turret"));
            case GameEvent.EventTypes.JungleProximity:
                d = EventJson.TryParseObject(details);
                return Escape(EventJson.ReadString(d, "who"));
            default:
                return "";
        }
    }

    /// <summary>2 s for every live point type (KILL DEATH ASSIST DRAGON BARON HERALD TURRET INHIBITOR
    /// FIRST_BLOOD MULTI_KILL LEVEL_UP FLASH SUMMONER_SPELL ALL_IN UNCERTAIN_COMBAT and unknown);
    /// 5 s TRADE, RECALL; 10 s JUNGLE_PROXIMITY, TEAMFIGHT.</summary>
    public static int ToleranceFor(string eventType) => (eventType ?? "").Trim().ToUpperInvariant() switch
    {
        GameEvent.EventTypes.Trade or GameEvent.EventTypes.Recall => 5,
        GameEvent.EventTypes.JungleProximity or GameEvent.TrackableTokens.TeamfightToken => 10,
        _ => 2,
    };

    /// <summary>(Type, Anchor, Disc unescaped, Ordinal 1-based) for det: keys; null otherwise.</summary>
    public static (string Type, int Anchor, string Disc, int Ordinal)? Parse(string key)
    {
        if (key is null || !key.StartsWith(DetectedPrefix, StringComparison.Ordinal)) return null;
        var parts = key.Substring(DetectedPrefix.Length).Split(':', 3);
        if (parts.Length < 3 || parts[0].Length == 0) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var anchor)) return null;
        var disc = parts[2];
        var ordinal = 1;
        var hash = disc.LastIndexOf('#');
        if (hash >= 0 && hash < disc.Length - 1
            && int.TryParse(disc.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n >= 2)
        {
            ordinal = n;
            disc = disc.Substring(0, hash);
        }
        return (parts[0], anchor, Unescape(disc), ordinal);
    }

    /// <summary>Match order for one applicable correction against a candidate list:
    /// 1) exact key equality with SubjectKey (unclaimed);
    /// 2) same type as SubjectType, |AnchorOf(cand) - SubjectTimeS| &lt;= tolerance, disc agreeing
    ///    (either side empty counts as agreeing), unique nearest among unclaimed;
    /// 3) the same rule against (EffectiveType, EffectiveTimeS);
    /// op add uses rule 3 only (type = Patch.EventType, disc ignored).
    /// A tie at the minimal distance yields null. Never matches across types.
    /// Candidates carrying a correction marker or source=reviewed_encounter are never matched.</summary>
    public static EventMatch? FindMatch(
        EventCorrection c,
        IReadOnlyList<GameEvent> candidates,
        IReadOnlyList<string> candidateKeys,
        ISet<int> claimed)
    {
        var eligible = new bool[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            var details = candidates[i].Details ?? "";
            if (EventPatching.HasMarker(details)) continue;
            if (EventJson.ReadString(EventJson.TryParseObject(details), "source") == ReviewedEncountersRepository.Source) continue;
            eligible[i] = true;
        }

        var subjectDisc = Parse(c.SubjectKey)?.Disc ?? "";

        if (c.Op != CorrectionOps.Add)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!eligible[i]) continue;
                if (i < candidateKeys.Count && string.Equals(candidateKeys[i], c.SubjectKey, StringComparison.Ordinal))
                    return new EventMatch(i, true, candidateKeys[i]);
            }

            var detected = Nearest(candidates, candidateKeys, eligible, c.SubjectType, c.SubjectTimeS, subjectDisc);
            if (detected is not null) return detected;
        }

        var effectiveType = c.Op == CorrectionOps.Add ? (c.Patch.EventType ?? c.SubjectType) : c.EffectiveType;
        // The discriminator names a per-type field (victim for KILL, killer for DEATH), so it only
        // constrains a candidate of the SAME type as the subject; a retype to another type has no
        // comparable name and matches on type + time alone. Adds ignore it by contract.
        var disc = c.Op != CorrectionOps.Add && string.Equals(effectiveType, c.SubjectType, StringComparison.OrdinalIgnoreCase)
            ? subjectDisc
            : "";
        return Nearest(candidates, candidateKeys, eligible, effectiveType, c.EffectiveTimeS, disc);
    }

    private static EventMatch? Nearest(
        IReadOnlyList<GameEvent> candidates, IReadOnlyList<string> candidateKeys, bool[] eligible,
        string type, int timeS, string disc)
    {
        var tolerance = ToleranceFor(type);
        var best = -1;
        var bestDistance = int.MaxValue;
        var tie = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!eligible[i]) continue;
            var cand = candidates[i];
            if (!string.Equals(cand.EventType, type, StringComparison.OrdinalIgnoreCase)) continue;
            var distance = Math.Abs(AnchorOf(cand) - timeS);
            if (distance > tolerance) continue;
            if (disc.Length > 0)
            {
                var candDisc = Unescape(DiscOf(cand.EventType, cand.Details));
                if (candDisc.Length > 0 && !string.Equals(candDisc, disc, StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
                tie = false;
            }
            else if (distance == bestDistance)
            {
                tie = true;
            }
        }
        if (best < 0 || tie) return null;
        var key = best < candidateKeys.Count ? candidateKeys[best] : KeyFor(candidates[best]);
        return new EventMatch(best, false, key);
    }

    /// <summary>The detector reproduced the fix: type == Patch.EventType ?? Original.EventType;
    /// Patch.GameTimeS null or |AnchorOf(row) - it| &lt;= 1; Patch.EndS null or |details.end_s ?? anchor - it| &lt;= 1;
    /// every Patch.Attrs key equal after Normalize (missing, null, false, "" all normalize to "";
    /// true to "true"; strings trimmed lower; numbers invariant). Confirmed is ignored.</summary>
    public static bool Satisfies(GameEvent row, EventPatch patch, EventOriginal original)
    {
        var wantType = patch.EventType ?? original.EventType;
        if (!string.Equals(row.EventType, wantType, StringComparison.OrdinalIgnoreCase)) return false;
        var anchor = AnchorOf(row);
        if (patch.GameTimeS is { } t && Math.Abs(anchor - t) > 1) return false;
        var details = EventJson.TryParseObject(row.Details);
        if (patch.EndS is { } end)
        {
            var rowEnd = EventJson.ReadInt(details, "end_s") ?? anchor;
            if (Math.Abs(rowEnd - end) > 1) return false;
        }
        if (patch.Attrs is not null)
        {
            foreach (var pair in patch.Attrs)
            {
                if (!string.Equals(Normalize(details?[pair.Key]), Normalize(pair.Value), StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    public static string Normalize(JsonNode? value)
    {
        if (value is null) return "";
        if (value is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "";
            if (v.TryGetValue<string>(out var s)) return s.Trim().ToLowerInvariant();
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
            return v.ToJsonString();
        }
        return value.ToJsonString();
    }

    private static string Escape(string? value)
    {
        var s = (value ?? "").Trim();
        if (s.Length == 0) return "";
        return s.Replace(":", "%3A", StringComparison.Ordinal).Replace("#", "%23", StringComparison.Ordinal);
    }

    private static string Unescape(string value) =>
        value.Replace("%3A", ":", StringComparison.OrdinalIgnoreCase).Replace("%23", "#", StringComparison.OrdinalIgnoreCase);
}
