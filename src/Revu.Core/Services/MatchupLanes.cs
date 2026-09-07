#nullable enable

using Revu.Core.Constants;

namespace Revu.Core.Services;

/// <summary>
/// The matchup journal's lane vocabulary and the champion-list conventions
/// every surface (repository validation, last-game pre-fill, page snapshot,
/// Markdown export) shares, so a card reads identically everywhere.
///
/// <para>Convention: top and mid are 1v1. Jungle is the 2v2 of jungler + mid;
/// bot and support are the 2v2 of adc + support (same pair, listed adc first
/// on both lanes). Champion names go through
/// <see cref="GameConstants.CanonicalChampionName"/> so a card pre-filled from
/// a backfilled map ("Kaisa", "Renata") and one typed by hand ("Kai'Sa",
/// "Renata Glasc") land in the same group.</para>
/// </summary>
public static class MatchupLanes
{
    public const string Top = "top";
    public const string Jungle = "jungle";
    public const string Mid = "mid";
    public const string Bot = "bot";
    public const string Support = "support";

    /// <summary>Every lane, in the display order the page and the export use.</summary>
    public static readonly IReadOnlyList<string> All = [Top, Jungle, Mid, Bot, Support];

    /// <summary>Lower-cased, trimmed lane value, or null when it is not a lane.</summary>
    public static string? Normalize(string? lane)
    {
        var value = lane?.Trim().ToLowerInvariant() ?? "";
        return All.Contains(value) ? value : null;
    }

    public static bool IsValid(string? lane) => Normalize(lane) is not null;

    /// <summary>Champions per side: 1 for top / mid (1v1), 2 for jungle / bot / support (2v2).</summary>
    public static int ChampSlots(string? lane) => Normalize(lane) is Top or Mid ? 1 : 2;

    /// <summary>Display label ("Top", "Jungle", …); empty for an unknown lane.</summary>
    public static string Label(string? lane) => Normalize(lane) switch
    {
        Top => "Top",
        Jungle => "Jungle",
        Mid => "Mid",
        Bot => "Bot",
        Support => "Support",
        _ => "",
    };

    /// <summary>Position in <see cref="All"/>; unknown lanes sort last.</summary>
    public static int Order(string? lane)
    {
        var value = Normalize(lane);
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == value) return i;
        }
        return All.Count;
    }

    /// <summary>
    /// Lane for the position a game row carries — the LCU form
    /// (TOP / JUNGLE / MIDDLE / BOTTOM / UTILITY) or the config short names
    /// (top / jg / mid / adc / supp) — or null when it is unknown or blank.
    /// </summary>
    public static string? FromPosition(string? position) => position?.Trim().ToLowerInvariant() switch
    {
        "top" => Top,
        "jungle" or "jg" => Jungle,
        "mid" or "middle" => Mid,
        "adc" or "bottom" or "bot" => Bot,
        "support" or "supp" or "utility" => Support,
        _ => null,
    };

    /// <summary>
    /// Trim, drop blanks, and canonicalize a champion list. The count is the
    /// caller's to enforce (the repository requires 1..<see cref="ChampSlots"/>).
    /// </summary>
    public static IReadOnlyList<string> NormalizeChampions(IEnumerable<string?>? champions)
    {
        var list = new List<string>();
        foreach (var champion in champions ?? [])
        {
            var name = GameConstants.CanonicalChampionName(champion);
            if (name.Length > 0) list.Add(name);
        }
        return list;
    }

    /// <summary>"Kai'Sa + Nautilus vs Tristana + Renata Glasc".</summary>
    public static string Title(IReadOnlyList<string> allyChamps, IReadOnlyList<string> enemyChamps) =>
        $"{Side(allyChamps)} vs {Side(enemyChamps)}";

    /// <summary>
    /// Grouping identity of a matchup: lane + the normalized names of each side,
    /// e.g. <c>bot|kaisa+nautilus|tristana+renataglasc</c>. Slot order is part
    /// of the key on purpose — every producer stores slot order, so it never
    /// varies for the same matchup.
    /// </summary>
    public static string Key(string lane, IReadOnlyList<string> allyChamps, IReadOnlyList<string> enemyChamps) =>
        $"{Normalize(lane) ?? lane.Trim().ToLowerInvariant()}|{SideKey(allyChamps)}|{SideKey(enemyChamps)}";

    // Both go through the canonicalizer so a raw record (an id-form "Renata" from
    // a backfilled map, a hand-typed "Kaisa") reads and groups like the stored,
    // already-canonical rows do — the repository canonicalizes on write, but the
    // title / key must not depend on that.
    private static string Side(IReadOnlyList<string> champs) =>
        champs.Count == 0 ? "?" : string.Join(" + ", champs.Select(GameConstants.CanonicalChampionName));

    private static string SideKey(IReadOnlyList<string> champs) =>
        string.Join("+", champs.Select(c => GameConstants.NormalizeChampionKey(GameConstants.CanonicalChampionName(c))));
}
