#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Lcu;

/// <summary>
/// v3.10.1: last-resort matchup sources for a just-captured game whose
/// end-of-game payload AND live roster left the lane / opponent / role map
/// blank. Each method fills ONLY blank fields, so callers apply them in
/// priority order and the better source always wins; each stamps
/// <see cref="GameStats.MatchupSource"/> with an estimate marker
/// (<see cref="MatchupSources.NeedsConfirmation"/>) so the Match-V5 pass still
/// confirms or corrects the row a minute later. The point is that the Review
/// hero, the dashboard card and the Matchups journal have something to show the
/// moment the game ends instead of a bare champion name.
/// </summary>
public static class MatchupFallback
{
    private static readonly string[] Positions = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];
    private static readonly string[] KeySuffixes = ["Top", "Jg", "Mid", "Bot", "Supp"];

    /// <summary>
    /// The champ-select snapshot (<c>LcuClient.GetChampSelectSnapshotAsync</c>):
    /// the player's <c>assignedPosition</c> and a role→champion map whose own side
    /// is the lobby's assignment and whose enemy side is the role-prior estimate.
    /// Refused when the map does not hold the player's champion on the own side —
    /// that is another lobby's snapshot (a dodge, a remake, an app started
    /// mid-game). Returns true when anything landed.
    /// </summary>
    public static bool ApplyChampSelect(GameStats? game, string? myPosition, string? participantMapJson)
    {
        if (game is null) return false;
        var map = ParseMap(participantMapJson);
        if (map is null || map.Count == 0) return false;
        if (!OwnSideHolds(map, game.ChampionName)) return false;

        var position = LiveRoster.NormalizePosition(myPosition);
        if (position.Length == 0) position = PositionFromOwnSlot(map, game.ChampionName);

        var applied = false;
        if (game.Position.Length == 0 && position.Length > 0)
        {
            game.Position = position;
            applied = true;
        }
        if (game.ParticipantMap.Length == 0)
        {
            game.ParticipantMap = JsonSerializer.Serialize(map);
            applied = true;
        }
        var lane = position.Length > 0 ? position : game.Position;
        if (game.EnemyLaner.Length == 0)
        {
            var enemy = EnemyAt(map, lane);
            if (enemy.Length > 0)
            {
                game.EnemyLaner = enemy;
                applied = true;
            }
        }

        if (applied && !MatchupSources.IsConfirmed(game.MatchupSource))
            game.MatchupSource = MatchupSources.ChampSelect;
        return applied;
    }

    /// <summary>
    /// Both sides estimated from champion role priors (<see cref="RoleAssignment"/>)
    /// over the champion lists the capture stored in <c>RawStats</c>
    /// (<c>_own_champions</c> / <c>_enemy_champions</c>). Summoner's Rift 5v5
    /// only — a comp of five per side in a CLASSIC game; anything else (ARAM,
    /// a custom, a short list) is refused rather than guessed. Returns true when
    /// anything landed.
    /// </summary>
    public static bool ApplyRolePriors(GameStats? game)
    {
        if (game is null) return false;
        if (!IsSummonersRiftFiveVsFive(game, out var own, out var enemy)) return false;

        var ownByRole = RoleAssignment.AssignRoles(own);
        var enemyByRole = RoleAssignment.AssignRoles(enemy);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < RoleAssignment.RoleCount; i++)
        {
            if (ownByRole[i].Length > 0) map["own" + KeySuffixes[i]] = ownByRole[i];
            if (enemyByRole[i].Length > 0) map["enemy" + KeySuffixes[i]] = enemyByRole[i];
        }

        var myIndex = Array.FindIndex(ownByRole, c => SameChampion(c, game.ChampionName));

        var applied = false;
        if (game.Position.Length == 0 && myIndex >= 0)
        {
            game.Position = Positions[myIndex];
            applied = true;
        }
        if (game.ParticipantMap.Length == 0 && map.Count > 0)
        {
            game.ParticipantMap = JsonSerializer.Serialize(map);
            applied = true;
        }
        if (game.EnemyLaner.Length == 0)
        {
            var laneIndex = myIndex >= 0 ? myIndex : IndexOfPosition(game.Position);
            if (laneIndex >= 0 && enemyByRole[laneIndex].Length > 0)
            {
                game.EnemyLaner = enemyByRole[laneIndex];
                applied = true;
            }
        }

        if (applied && !MatchupSources.IsConfirmed(game.MatchupSource))
            game.MatchupSource = MatchupSources.Heuristic;
        return applied;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsSummonersRiftFiveVsFive(GameStats game, out List<string> own, out List<string> enemy)
    {
        own = ReadNames(game.RawStats, "_own_champions");
        enemy = ReadNames(game.RawStats, "_enemy_champions");
        if (own.Count != 5 || enemy.Count != 5) return false;

        // The raw game mode is stamped at capture; a row without it (an older
        // capture path) must at least be a queue the app treats as Summoner's Rift.
        var mode = ReadString(game.RawStats, "_game_mode_raw");
        if (mode.Length > 0) return string.Equals(mode, "CLASSIC", StringComparison.OrdinalIgnoreCase);
        var queue = GameConstants.NormalizeQueueLabel(game.QueueType);
        return GameConstants.RankedQueueTypes.Contains(queue)
            || GameConstants.CasualQueueTypes.Contains(queue)
            || string.Equals(queue, "Ranked Flex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A RawStats list is a <c>List&lt;string&gt;</c> right after capture and a
    /// <c>JsonElement</c> array after a database round-trip; read either.
    /// </summary>
    private static List<string> ReadNames(IReadOnlyDictionary<string, object> raw, string key)
    {
        var names = new List<string>();
        if (!raw.TryGetValue(key, out var value) || value is null) return names;
        switch (value)
        {
            case IEnumerable<string> list:
                foreach (var s in list) if (!string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } el:
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
                }
                break;
        }
        return names;
    }

    private static string ReadString(IReadOnlyDictionary<string, object> raw, string key)
    {
        if (!raw.TryGetValue(key, out var value) || value is null) return "";
        return value switch
        {
            string s => s.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } el => (el.GetString() ?? "").Trim(),
            _ => "",
        };
    }

    private static Dictionary<string, string>? ParseMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (map is null) return null;
            var clean = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in map)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) clean[k] = v.Trim();
            }
            return clean;
        }
        catch
        {
            return null;
        }
    }

    private static bool OwnSideHolds(Dictionary<string, string> map, string champion) =>
        map.Any(kv => kv.Key.StartsWith("own", StringComparison.Ordinal) && SameChampion(kv.Value, champion));

    private static string PositionFromOwnSlot(Dictionary<string, string> map, string champion)
    {
        for (var i = 0; i < KeySuffixes.Length; i++)
        {
            if (map.TryGetValue("own" + KeySuffixes[i], out var c) && SameChampion(c, champion)) return Positions[i];
        }
        return "";
    }

    private static string EnemyAt(Dictionary<string, string> map, string position)
    {
        var i = IndexOfPosition(position);
        return i >= 0 && map.TryGetValue("enemy" + KeySuffixes[i], out var c) ? c : "";
    }

    private static int IndexOfPosition(string? position)
    {
        var normalized = LiveRoster.NormalizePosition(position);
        return normalized.Length == 0 ? -1 : Array.IndexOf(Positions, normalized);
    }

    private static bool SameChampion(string? a, string? b) =>
        LiveRoster.ChampionKey(GameConstants.CanonicalChampionName(a ?? ""))
            == LiveRoster.ChampionKey(GameConstants.CanonicalChampionName(b ?? ""));
}
