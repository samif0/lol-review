#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Lane + champion lists a matchup card can be pre-filled with from a game.
///
/// <para><see cref="AllyChamps"/> / <see cref="EnemyChamps"/> are the names
/// known — what the card's lists hold. <see cref="AllySlots"/> /
/// <see cref="EnemySlots"/> are the same names BY FORM SLOT (adc first, support
/// second; jungler first, mid second; "" where the slot is unknown), so a form
/// pre-filled from a partial result never loses which slot a lone name belongs
/// to — a support's own champion lands in the support field, not the ADC's.</para>
///
/// <para><see cref="IsComplete"/> is false when the enemy side is unknown (a
/// game recovered from the client's match history without positions).
/// <see cref="LaneIsGuess"/> is true when the lane came from the caller's
/// fallback (the player's configured primary role) rather than from the game
/// itself. In either case the card should go through the form
/// (<see cref="CanCreateOutright"/> is false) so the player confirms it.</para>
/// </summary>
public sealed record MatchupPrefillResult(
    string Lane,
    IReadOnlyList<string> AllyChamps,
    IReadOnlyList<string> EnemyChamps,
    IReadOnlyList<string> AllySlots,
    IReadOnlyList<string> EnemySlots,
    bool LaneIsGuess)
{
    /// <summary>Both sides have at least one known champion.</summary>
    public bool IsComplete => AllyChamps.Count > 0 && EnemyChamps.Count > 0;

    /// <summary>Everything the card needs came from the game itself — create it without the form.</summary>
    public bool CanCreateOutright => IsComplete && !LaneIsGuess;

    public string Title => MatchupLanes.Title(AllyChamps, EnemyChamps);
}

/// <summary>
/// "New card from last game": derive the lane and the 1v1 / 2v2 champion
/// lists from a saved game's participant map — the role→champion JSON stamped
/// at game end (<c>StatsExtractor</c>) or by the Match-V5 backfill
/// (<c>EnemyLanerBackfillService.ExtractParticipantMap</c>). Pure, so the
/// sidecar's read snapshot (the button's preview) and its write route (the
/// card it creates) can never disagree about what the card contains.
///
/// <para>Convention is the journal's, not <see cref="MatchupDisplay"/>'s: top
/// and mid are 1v1; jungle is jungler + mid; bot and support are adc +
/// support. Degrades gracefully — the game's own <c>champion_name</c> /
/// <c>enemy_laner</c> fill a side the map lacks; a game with no position
/// resolves its lane from whichever own-side slot holds the played champion,
/// then from the caller's <c>fallbackPosition</c> (the player's configured
/// primary role, flagged as a guess); and an unknown enemy side yields a
/// PARTIAL result rather than nothing. Null only when the lane can't be told
/// at all.</para>
///
/// <para>Bot vs support is settled by the map when it can be: a row's position
/// can be a bare "BOTTOM" (a game recovered from the client's match history,
/// which has no per-player position, stores the lane for both duo members) or
/// come from Riot's carry/support heuristic, while the map — once the Match-V5
/// backfill has written it from <c>teamPosition</c> — says which slot the
/// player actually held. The player's own slot wins; failing that, an ADC slot
/// already taken by a teammate means the player was the support, and vice
/// versa. Top / jungle / mid are never second-guessed.</para>
/// </summary>
public static class MatchupPrefill
{
    public static MatchupPrefillResult? FromGame(GameStats? game, string? fallbackPosition = null)
    {
        if (game is null) return null;

        var map = ParseMap(game.ParticipantMap);
        var champion = GameConstants.CanonicalChampionName(game.ChampionName);

        var laneIsGuess = false;
        var lane = MatchupLanes.FromPosition(game.Position) ?? InferLane(map, champion);
        if (lane is null)
        {
            lane = MatchupLanes.FromPosition(fallbackPosition);
            laneIsGuess = lane is not null;
        }
        if (lane is null) return null;
        lane = RefineDuoLane(lane, map, champion);

        var (ownKeys, enemyKeys) = SlotKeys(lane);
        // The player's own champion — and their direct opponent (enemy_laner) —
        // sit in the second slot only on the support lane (adc + support);
        // everywhere else the row's name is the primary slot.
        var ownIndex = lane == MatchupLanes.Support ? 1 : 0;
        var allySlots = Slots(map, ownKeys, champion, ownIndex);
        var enemySlots = Slots(map, enemyKeys, game.EnemyLaner, ownIndex);
        var ally = Known(allySlots);
        var enemy = Known(enemySlots);
        if (ally.Count == 0) return null;

        return new MatchupPrefillResult(lane, ally, enemy, allySlots, enemySlots, laneIsGuess);
    }

    private static readonly string[] OwnTop = ["ownTop"], EnemyTop = ["enemyTop"];
    private static readonly string[] OwnMid = ["ownMid"], EnemyMid = ["enemyMid"];
    private static readonly string[] OwnJungle = ["ownJg", "ownMid"], EnemyJungle = ["enemyJg", "enemyMid"];
    private static readonly string[] OwnBotLane = ["ownBot", "ownSupp"], EnemyBotLane = ["enemyBot", "enemySupp"];

    private static (string[] Own, string[] Enemy) SlotKeys(string lane) => lane switch
    {
        MatchupLanes.Top => (OwnTop, EnemyTop),
        MatchupLanes.Mid => (OwnMid, EnemyMid),
        MatchupLanes.Jungle => (OwnJungle, EnemyJungle),
        _ => (OwnBotLane, EnemyBotLane),
    };

    /// <summary>
    /// One side of the matchup by form slot: the map's champion for each slot
    /// key ("" when absent), with the game row's own name (champion_name /
    /// enemy_laner) filling its preferred slot — or the first empty one — when
    /// the map lacks it. Never a duplicate; a name already in the map stays
    /// where the map put it.
    /// </summary>
    private static string[] Slots(Dictionary<string, string> map, string[] keys, string fallback, int preferredIndex)
    {
        var slots = new string[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            slots[i] = map.TryGetValue(keys[i], out var raw) ? GameConstants.CanonicalChampionName(raw) : "";
            // The same champion in two slots of one side is a corrupt map; keep the first.
            if (slots[i].Length > 0 && IndexOf(slots, slots[i], i) >= 0) slots[i] = "";
        }

        var fill = GameConstants.CanonicalChampionName(fallback);
        if (fill.Length > 0 && IndexOf(slots, fill, slots.Length) < 0)
        {
            var at = preferredIndex < slots.Length && slots[preferredIndex].Length == 0
                ? preferredIndex
                : Array.FindIndex(slots, s => s.Length == 0);
            if (at >= 0) slots[at] = fill;
        }

        return slots;
    }

    private static IReadOnlyList<string> Known(string[] slots) =>
        slots.Where(s => s.Length > 0).ToList();

    // Index of `name` among the first `count` slots (spelling-insensitive), or -1.
    private static int IndexOf(string[] slots, string name, int count)
    {
        var key = GameConstants.NormalizeChampionKey(name);
        for (var i = 0; i < count; i++)
        {
            if (slots[i].Length > 0 && GameConstants.NormalizeChampionKey(slots[i]) == key) return i;
        }
        return -1;
    }

    // No position on the row (older manual / imported games): the lane is
    // whichever own-side slot holds the champion the player played.
    private static string? InferLane(Dictionary<string, string> map, string championName)
    {
        var key = GameConstants.NormalizeChampionKey(championName);
        if (key.Length == 0) return null;
        foreach (var (slot, lane) in OwnSlots)
        {
            if (map.TryGetValue(slot, out var raw) && GameConstants.NormalizeChampionKey(raw) == key)
                return lane;
        }
        return null;
    }

    // Bot vs support from the map (see the class remarks). Only ever moves a
    // lane between those two; anything else is returned untouched.
    private static string RefineDuoLane(string lane, Dictionary<string, string> map, string championName)
    {
        if (lane != MatchupLanes.Bot && lane != MatchupLanes.Support) return lane;

        if (SlotHolds(map, "ownBot", championName)) return MatchupLanes.Bot;
        if (SlotHolds(map, "ownSupp", championName)) return MatchupLanes.Support;

        var adcTaken = SlotFilled(map, "ownBot");
        var supportTaken = SlotFilled(map, "ownSupp");
        if (lane == MatchupLanes.Bot && adcTaken && !supportTaken) return MatchupLanes.Support;
        if (lane == MatchupLanes.Support && supportTaken && !adcTaken) return MatchupLanes.Bot;
        return lane;
    }

    private static bool SlotHolds(Dictionary<string, string> map, string slot, string championName)
    {
        var key = GameConstants.NormalizeChampionKey(championName);
        return key.Length > 0
            && map.TryGetValue(slot, out var raw)
            && GameConstants.NormalizeChampionKey(raw) == key;
    }

    private static bool SlotFilled(Dictionary<string, string> map, string slot) =>
        map.TryGetValue(slot, out var raw) && GameConstants.CanonicalChampionName(raw).Length > 0;

    private static readonly (string Slot, string Lane)[] OwnSlots =
    [
        ("ownTop", MatchupLanes.Top),
        ("ownJg", MatchupLanes.Jungle),
        ("ownMid", MatchupLanes.Mid),
        ("ownBot", MatchupLanes.Bot),
        ("ownSupp", MatchupLanes.Support),
    ];

    private static Dictionary<string, string> ParseMap(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
