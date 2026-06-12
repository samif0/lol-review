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
            ? championName
            : $"{championName} vs {enemyChampion}";

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

    private static string? Pair(
        Dictionary<string, string> map,
        string ownPrimary, string ownPartner,
        string enemyPrimary, string enemyPartner)
    {
        if (!map.TryGetValue(ownPrimary, out var op) || string.IsNullOrEmpty(op)) return null;
        if (!map.TryGetValue(enemyPrimary, out var ep) || string.IsNullOrEmpty(ep)) return null;

        var ownPart = map.TryGetValue(ownPartner, out var v1) ? v1 : "";
        var enemyPart = map.TryGetValue(enemyPartner, out var v2) ? v2 : "";

        var ownStr = string.IsNullOrEmpty(ownPart) ? op : $"{op}+{ownPart}";
        var enemyStr = string.IsNullOrEmpty(enemyPart) ? ep : $"{ep}+{enemyPart}";
        return $"{ownStr} vs {enemyStr}";
    }
}
