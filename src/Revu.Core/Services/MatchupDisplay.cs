#nullable enable

using System.Collections.Generic;
using System.Text.Json;

namespace Revu.Core.Services;

/// <summary>
/// Role-aware matchup string shared by every surface that shows "you vs enemy"
/// for a game (games list, post-game review, VOD header, session logger).
/// Centralised in Core so the title reads identically everywhere AND the
/// bot/supp slot-keying — which has regressed before (v2.17.25) — lives in
/// exactly one place that the test suite pins directly.
///
/// Pairing rules: ADC shows the 2v2 with its support, support shows it with its
/// ADC, mid shows the 2v2 with the jungler (and vice-versa). Top has no obvious
/// adjacent partner, so it stays a 1v1. Falls back to "champ vs enemy" (or just
/// "champ") whenever the participant map can't produce a pairing.
/// </summary>
public static class MatchupDisplay
{
    /// <summary>
    /// Best matchup string for a game. <paramref name="role"/> is the role the
    /// user played (LCU position like BOTTOM/UTILITY/MIDDLE/JUNGLE/TOP, or the
    /// config short names adc/supp/mid/jg/top). <paramref name="participantMapJson"/>
    /// is the role→champion map captured at game end.
    /// </summary>
    public static string Build(
        string championName,
        string enemyChampion,
        string role,
        string participantMapJson)
    {
        return RoleAware(role, participantMapJson) ?? LaneOnly(championName, enemyChampion);
    }

    private static string LaneOnly(string championName, string enemyChampion) =>
        string.IsNullOrWhiteSpace(enemyChampion)
            ? Champ(championName)
            : $"{Champ(championName)} vs {Champ(enemyChampion)}";

    private static string? RoleAware(string role, string participantMapJson)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(participantMapJson))
            return null;

        var map = ParseMap(participantMapJson);
        if (map is null || map.Count == 0) return null;

        return role.ToLowerInvariant() switch
        {
            "adc" or "bottom" or "bot" =>
                Pair(map, "ownBot", "ownSupp", "enemyBot", "enemySupp"),
            "support" or "supp" or "utility" =>
                Pair(map, "ownSupp", "ownBot", "enemySupp", "enemyBot"),
            "mid" or "middle" =>
                Pair(map, "ownMid", "ownJg", "enemyMid", "enemyJg"),
            "jungle" or "jg" =>
                Pair(map, "ownJg", "ownMid", "enemyJg", "enemyMid"),
            // Top (and anything unrecognised): no adjacent pairing → 1v1.
            _ => null,
        };
    }

    private static Dictionary<string, string>? ParseMap(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Full-lobby matchup rows (champions only) from the participant map, in
    /// fixed lane order TOP/JG/MID/BOT/SUP. Lanes absent from the map on BOTH
    /// sides are omitted; a lane missing one side keeps the other with an empty
    /// string for the gap. Empty list when there is no usable map, so callers
    /// can hide the whole strip.
    /// </summary>
    public static IReadOnlyList<LobbyMatchupRow> LobbyRows(string role, string participantMapJson)
    {
        if (string.IsNullOrWhiteSpace(participantMapJson))
            return Array.Empty<LobbyMatchupRow>();

        var map = ParseMap(participantMapJson);
        if (map is null || map.Count == 0) return Array.Empty<LobbyMatchupRow>();

        var userSlot = UserSlot(role);
        var rows = new List<LobbyMatchupRow>(LaneSlots.Length);
        foreach (var (slot, label) in LaneSlots)
        {
            var own = map.TryGetValue("own" + slot, out var o) ? Champ(o) : "";
            var enemy = map.TryGetValue("enemy" + slot, out var e) ? Champ(e) : "";
            if (string.IsNullOrEmpty(own) && string.IsNullOrEmpty(enemy)) continue;
            rows.Add(new LobbyMatchupRow(label, own, enemy, slot == userSlot));
        }

        return rows;
    }

    // Suffixes of the participant-map keys (own/enemy + slot) with their display
    // labels, in the fixed top-to-bottom lane order every LoL scoreboard uses.
    private static readonly (string Slot, string Label)[] LaneSlots =
    [
        ("Top", "TOP"), ("Jg", "JG"), ("Mid", "MID"), ("Bot", "BOT"), ("Supp", "SUP"),
    ];

    private static string? UserSlot(string role) => role?.ToLowerInvariant() switch
    {
        "top" => "Top",
        "jungle" or "jg" => "Jg",
        "mid" or "middle" => "Mid",
        "adc" or "bottom" or "bot" => "Bot",
        "support" or "supp" or "utility" => "Supp",
        _ => null,
    };

    private static string? Pair(
        Dictionary<string, string> map,
        string ownPrimary, string ownPartner,
        string enemyPrimary, string enemyPartner)
    {
        if (!map.TryGetValue(ownPrimary, out var op) || string.IsNullOrEmpty(op)) return null;
        if (!map.TryGetValue(enemyPrimary, out var ep) || string.IsNullOrEmpty(ep)) return null;

        var ownPart = map.TryGetValue(ownPartner, out var v1) ? v1 : "";
        var enemyPart = map.TryGetValue(enemyPartner, out var v2) ? v2 : "";

        op = Champ(op);
        ep = Champ(ep);
        ownPart = Champ(ownPart);
        enemyPart = Champ(enemyPart);

        var ownStr = string.IsNullOrEmpty(ownPart) ? op : $"{op}+{ownPart}";
        var enemyStr = string.IsNullOrEmpty(enemyPart) ? ep : $"{ep}+{enemyPart}";
        return $"{ownStr} vs {enemyStr}";
    }

    /// <summary>
    /// Display repair at the output boundary: participant maps written by the
    /// Match-V5 backfill carry Riot's id-form names ("LeeSin", "Kaisa",
    /// "MonkeyKing") while LCU-captured ones carry display names; every name
    /// this class emits goes through the shared canonicalizer so both read
    /// "Lee Sin" / "Kai'Sa" / "Wukong". Empty stays empty.
    /// </summary>
    private static string Champ(string name) =>
        Revu.Core.Constants.GameConstants.CanonicalChampionName(name);
}

/// <summary>
/// One lane of the full-lobby matchup strip: "TOP  Aatrox vs Sett". Champions
/// only (the participant map never stores summoner names). IsUserLane marks the
/// lane the user played so the UI can highlight it.
/// </summary>
public sealed record LobbyMatchupRow(string RoleLabel, string Own, string Enemy, bool IsUserLane);
