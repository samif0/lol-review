#nullable enable

using System.Text;
using System.Text.Json;

namespace Revu.Core.Lcu;

/// <summary>
/// 3.14.1: reads League's own early-surrender (remake) flag from the three payloads Revu
/// sees. Pure. Only Riot's flag marks a remake; duration, KDA or CS never do. Every
/// reader returns null when the payload does not carry the flag (unknown), so a caller
/// can tell "not a remake" from "not said".
/// </summary>
internal static class RemakeSignals
{
    private const string EogTopLevel = "gameEndedInEarlySurrender";
    private const string EogStat = "GAME_ENDED_IN_EARLY_SURRENDER";
    private const string MatchField = "gameEndedInEarlySurrender";

    /// <summary>
    /// End-of-game stats block (<c>/lol-end-of-game/v1/eog-stats-block</c>): the top-level
    /// <c>gameEndedInEarlySurrender</c>, then <c>localPlayer.stats.GAME_ENDED_IN_EARLY_SURRENDER</c>.
    /// Any true means remake; a present false (and no true) means not a remake; neither
    /// present means null. CausedBySelf is stored for later policy only.
    /// </summary>
    public static (bool? Remake, bool CausedBySelf) FromEog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, false);

        var localPlayer = Child(root, "localPlayer");
        var stats = localPlayer is { } lp ? Child(lp, "stats") : null;

        var top = Flag(root, EogTopLevel);
        var stat = stats is { } s ? Flag(s, EogStat) : null;
        bool? remake = top == true || stat == true ? true
            : top is not null || stat is not null ? false
            : null;

        var caused = Flag(root, "causedEarlySurrender") == true
            || (localPlayer is { } p && (Flag(p, "causedEarlySurrender") == true || Flag(p, "leaver") == true))
            || (stats is { } st && (Flag(st, "CAUSED_EARLY_SURRENDER") == true || NonZero(st, "WAS_AFK")));
        return (remake, caused);
    }

    /// <summary>
    /// LCU match history (<c>/lol-match-history/v1/games/{id}</c>):
    /// <c>participants[self].stats.gameEndedInEarlySurrender</c>, falling back to any
    /// participant that carries it (the flag is game-wide). Missing means null.
    /// <paramref name="requireSelf"/>: a lookup by id (the remake sweep) accepts only the
    /// player's own participant (puuid, or participantIdentities puuid/currentPlayer), so
    /// a stranger's match fetched by a wrong id never sets a verdict.
    /// </summary>
    public static bool? FromLcuMatch(JsonElement game, string? puuid, JsonElement? self = null, bool requireSelf = false)
    {
        if (game.ValueKind != JsonValueKind.Object) return null;
        if (self is { } known && Child(known, "stats") is { } knownStats && Flag(knownStats, MatchField) is bool k)
            return k;
        if (!game.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array)
            return null;

        var selfId = SelfParticipantId(game, puuid);
        bool? any = null;
        foreach (var part in participants.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var flag = Child(part, "stats") is { } st ? Flag(st, MatchField) : null;
            if (flag is null) continue;
            var isSelf = (!string.IsNullOrWhiteSpace(puuid) && string.Equals(Str(part, "puuid"), puuid, StringComparison.Ordinal))
                || (selfId is int id && Int(part, "participantId") == id);
            if (isSelf) return flag;
            any ??= flag;
        }
        return requireSelf ? null : any;
    }

    /// <summary>
    /// Match-V5 (<c>info.participants[].gameEndedInEarlySurrender</c>): the self participant
    /// (<c>puuid</c>), falling back to the first participant that has the field. Never
    /// depends on <c>teamPosition</c>; remake documents often have blank positions.
    /// <paramref name="requireSelf"/>: only the player's own participant counts (lookups by id).
    /// </summary>
    public static bool? FromMatchV5(JsonElement match, string? puuid, bool requireSelf = false)
    {
        if (match.ValueKind != JsonValueKind.Object
            || !match.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array)
            return null;

        bool? any = null;
        foreach (var part in participants.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var flag = Flag(part, MatchField);
            if (flag is null) continue;
            if (!string.IsNullOrWhiteSpace(puuid) && string.Equals(Str(part, "puuid"), puuid, StringComparison.Ordinal))
                return flag;
            any ??= flag;
        }
        return requireSelf ? null : any;
    }

    /// <summary>Field names and raw values only, for the capture log. No real remake
    /// payload has been seen locally, so every flag the readers consult is logged.</summary>
    public static string DescribeEog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "eog=not-an-object";
        var localPlayer = Child(root, "localPlayer");
        var stats = localPlayer is { } lp ? Child(lp, "stats") : null;
        var sb = new StringBuilder();
        Append(sb, EogTopLevel, root);
        Append(sb, "causedEarlySurrender", root);
        Append(sb, "localPlayer.leaver", localPlayer, "leaver");
        Append(sb, "localPlayer.causedEarlySurrender", localPlayer, "causedEarlySurrender");
        Append(sb, "stats." + EogStat, stats, EogStat);
        Append(sb, "stats.CAUSED_EARLY_SURRENDER", stats, "CAUSED_EARLY_SURRENDER");
        Append(sb, "stats.WAS_AFK", stats, "WAS_AFK");
        return sb.ToString();
    }

    /// <summary>True/False, Number 0/1, String "0"/"1"/"true"/"false"; anything else null.</summary>
    public static bool? ReadFlag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var n) && n is 0 or 1 => n == 1,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => null,
        },
        _ => null,
    };

    private static bool? Flag(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) ? ReadFlag(value) : null;

    private static bool NonZero(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out var d) && d != 0;
        if (value.ValueKind == JsonValueKind.String)
            return double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed != 0;
        return value.ValueKind == JsonValueKind.True;
    }

    private static JsonElement? Child(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child : null;

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    // Legacy LCU shape: the puuid lives under participantIdentities[].player.
    private static int? SelfParticipantId(JsonElement game, string? puuid)
    {
        if (!game.TryGetProperty("participantIdentities", out var identities) || identities.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var identity in identities.EnumerateArray())
        {
            if (identity.ValueKind != JsonValueKind.Object || Child(identity, "player") is not { } player) continue;
            var matches = (!string.IsNullOrWhiteSpace(puuid) && string.Equals(Str(player, "puuid"), puuid, StringComparison.Ordinal))
                || Flag(player, "currentPlayer") == true;
            if (matches) return Int(identity, "participantId");
        }
        return null;
    }

    private static void Append(StringBuilder sb, string label, JsonElement? obj, string? field = null)
    {
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(label).Append('=');
        if (obj is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(field ?? label, out var value))
        {
            var raw = value.GetRawText();
            sb.Append(raw.Length > 40 ? raw[..40] : raw);
        }
        else
        {
            sb.Append("absent");
        }
    }
}
