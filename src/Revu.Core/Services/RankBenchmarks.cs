#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// v2.18: static per-rank / per-role benchmark table — the app's first piece
/// of external ground truth. Values are approximate averages for ranked
/// solo/duo drawn from public aggregate stats; they answer "is this number
/// bad, and compared to what?" which a player cannot answer from their own
/// 30-day baseline. Maintained by hand; revisit roughly once a season.
/// </summary>
public static class RankBenchmarks
{
    public sealed record Entry(
        double CsPerMin,
        double Deaths,
        double VisionScore,
        double KillParticipation);

    public static readonly IReadOnlyList<string> Ranks =
    [
        "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND", "MASTER+",
    ];

    public static readonly IReadOnlyList<string> Roles =
    [
        "TOP", "JUNGLE", "MID", "ADC", "SUPPORT",
    ];

    // Per-role arrays indexed by rank (Iron → Master+).
    private static readonly IReadOnlyDictionary<string, Entry[]> Table = new Dictionary<string, Entry[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["TOP"] =
        [
            new(4.4, 6.3, 14, 38), new(4.8, 6.1, 15, 39), new(5.1, 6.0, 16, 40), new(5.4, 5.9, 18, 41),
            new(5.7, 5.7, 20, 42), new(6.0, 5.6, 22, 43), new(6.4, 5.4, 24, 45), new(6.9, 5.2, 28, 47),
        ],
        ["JUNGLE"] =
        [
            new(3.6, 6.2, 22, 50), new(3.9, 6.0, 24, 51), new(4.2, 5.9, 26, 52), new(4.5, 5.8, 28, 54),
            new(4.8, 5.6, 30, 55), new(5.1, 5.5, 33, 56), new(5.4, 5.3, 36, 58), new(5.8, 5.1, 40, 60),
        ],
        ["MID"] =
        [
            new(4.6, 6.0, 14, 44), new(5.0, 5.9, 15, 45), new(5.4, 5.8, 16, 46), new(5.7, 5.7, 18, 48),
            new(6.0, 5.5, 20, 49), new(6.3, 5.4, 22, 50), new(6.7, 5.2, 24, 52), new(7.2, 5.0, 28, 55),
        ],
        ["ADC"] =
        [
            new(4.8, 6.0, 14, 42), new(5.2, 5.8, 15, 43), new(5.6, 5.7, 16, 44), new(6.0, 5.6, 17, 46),
            new(6.4, 5.4, 18, 47), new(6.7, 5.3, 20, 48), new(7.1, 5.1, 22, 50), new(7.6, 4.9, 26, 53),
        ],
        ["SUPPORT"] =
        [
            new(0.9, 6.6, 38, 48), new(1.0, 6.4, 42, 49), new(1.0, 6.3, 46, 50), new(1.1, 6.2, 50, 52),
            new(1.1, 6.0, 55, 53), new(1.2, 5.9, 60, 54), new(1.2, 5.7, 66, 56), new(1.3, 5.5, 75, 58),
        ],
    };

    /// <summary>Riot position string → benchmark role bucket. Empty when unmapped.</summary>
    public static string NormalizeRole(string? position) => position?.Trim().ToUpperInvariant() switch
    {
        "TOP" => "TOP",
        "JUNGLE" or "JG" => "JUNGLE",
        "MIDDLE" or "MID" => "MID",
        "BOTTOM" or "BOT" or "ADC" => "ADC",
        "UTILITY" or "SUPPORT" or "SUPP" => "SUPPORT",
        _ => "",
    };

    /// <summary>Unknown / empty ranks default to GOLD — the ladder median.</summary>
    public static string NormalizeRank(string? rank)
    {
        var normalized = rank?.Trim().ToUpperInvariant() ?? "";
        return Ranks.Contains(normalized) ? normalized : "GOLD";
    }

    /// <summary>
    /// Riot League-V4 tier ("IRON".."CHALLENGER") → benchmark rank.
    /// Apex tiers collapse into MASTER+; unranked/unknown returns "".
    /// </summary>
    public static string FromRiotTier(string? tier) => tier?.Trim().ToUpperInvariant() switch
    {
        "IRON" => "IRON",
        "BRONZE" => "BRONZE",
        "SILVER" => "SILVER",
        "GOLD" => "GOLD",
        "PLATINUM" => "PLATINUM",
        "EMERALD" => "EMERALD",
        "DIAMOND" => "DIAMOND",
        "MASTER" or "GRANDMASTER" or "CHALLENGER" => "MASTER+",
        _ => "",
    };

    /// <summary>The rank above (MASTER+ caps at itself).</summary>
    public static string NextRank(string rank)
    {
        var normalized = NormalizeRank(rank);
        var index = 0;
        for (var i = 0; i < Ranks.Count; i++)
        {
            if (Ranks[i] == normalized)
            {
                index = i;
                break;
            }
        }
        return Ranks[Math.Min(index + 1, Ranks.Count - 1)];
    }

    public static Entry? Get(string? position, string? rank)
    {
        var role = NormalizeRole(position);
        if (role.Length == 0 || !Table.TryGetValue(role, out var entries))
        {
            return null;
        }

        var normalizedRank = NormalizeRank(rank);
        for (var i = 0; i < Ranks.Count; i++)
        {
            if (Ranks[i] == normalizedRank)
            {
                return entries[i];
            }
        }
        return null;
    }

    /// <summary>Compact benchmark line, e.g. "CS/MIN 5.7 · DEATHS 5.9 · VISION 18 · KP 41%".</summary>
    public static string FormatLine(Entry entry)
        => $"CS/MIN {entry.CsPerMin:0.#} · DEATHS {entry.Deaths:0.#} · VISION {entry.VisionScore:0} · KP {entry.KillParticipation:0}%";
}
