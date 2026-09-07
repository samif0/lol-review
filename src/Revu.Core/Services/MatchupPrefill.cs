#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>Lane + champion lists a matchup card can be pre-filled with from a game.</summary>
public sealed record MatchupPrefillResult(
    string Lane,
    IReadOnlyList<string> AllyChamps,
    IReadOnlyList<string> EnemyChamps)
{
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
/// <c>enemy_laner</c> fill a side the map lacks, and a game with no position
/// still resolves its lane from whichever own-side slot holds the played
/// champion — so a card is offered whenever at least one champion is known on
/// each side. Null when nothing usable is stored.</para>
/// </summary>
public static class MatchupPrefill
{
    public static MatchupPrefillResult? FromGame(GameStats? game)
    {
        if (game is null) return null;

        var map = ParseMap(game.ParticipantMap);
        var lane = MatchupLanes.FromPosition(game.Position) ?? InferLane(map, game.ChampionName);
        if (lane is null) return null;

        var (ownSlots, enemySlots) = SlotKeys(lane);
        // The player's own champion sits in the second slot only on the support
        // lane (adc + support); everywhere else the fallback name is the primary.
        var fallbackIsSecondSlot = lane == MatchupLanes.Support;
        var ally = Side(map, ownSlots, game.ChampionName, fallbackIsSecondSlot);
        var enemy = Side(map, enemySlots, game.EnemyLaner, fallbackIsSecondSlot);
        if (ally.Count == 0 || enemy.Count == 0) return null;

        return new MatchupPrefillResult(lane, ally, enemy);
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
    /// One side of the matchup from the map's slots, with the game row's own
    /// name (champion_name / enemy_laner) filling in when the map lacks it.
    /// Never more than the lane's slot count, never a duplicate.
    /// </summary>
    private static IReadOnlyList<string> Side(
        Dictionary<string, string> map, string[] slots, string fallback, bool fallbackIsSecondSlot)
    {
        var names = new List<string>(slots.Length);
        foreach (var slot in slots)
        {
            if (!map.TryGetValue(slot, out var raw)) continue;
            var name = GameConstants.CanonicalChampionName(raw);
            if (name.Length > 0 && !Contains(names, name)) names.Add(name);
        }

        var fill = GameConstants.CanonicalChampionName(fallback);
        if (fill.Length > 0 && names.Count < slots.Length && !Contains(names, fill))
        {
            if (fallbackIsSecondSlot) names.Add(fill);
            else names.Insert(0, fill);
        }

        return names;
    }

    private static bool Contains(List<string> names, string candidate)
    {
        var key = GameConstants.NormalizeChampionKey(candidate);
        return names.Any(n => GameConstants.NormalizeChampionKey(n) == key);
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
