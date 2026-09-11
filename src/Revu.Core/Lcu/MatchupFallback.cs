#nullable enable

using System.Text.Json;
using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Lcu;

/// <summary>
/// v3.10.1: last-resort matchup sources for a just-captured game whose
/// end-of-game payload AND live roster left the lane / opponent / role map
/// blank. Each method fills ONLY blank fields, so callers apply them in
/// priority order and the better source always wins. Whatever they fill is an
/// estimate: the row is stamped with an estimate marker
/// (<see cref="MatchupSources.NeedsConfirmation"/>) even when the capture had
/// confirmed other fields, so the Match-V5 pass confirms or corrects it a
/// minute later. The point is that the Review hero, the dashboard card and the
/// Matchups journal have something to show the moment the game ends instead of
/// a bare champion name.
/// </summary>
public static class MatchupFallback
{
    private static readonly string[] Positions = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];
    private static readonly string[] KeySuffixes = ["Top", "Jg", "Mid", "Bot", "Supp"];

    /// <summary>
    /// The sidecar's game-end decision as one testable seam. A recovered game gets
    /// nothing: its row came from match history and no champ-select snapshot can
    /// be trusted to belong to it. The champ-select snapshot is used only when this
    /// flow had a session key; role priors then fill whatever is still blank.
    /// True when anything landed.
    /// </summary>
    public static bool ApplyForGameEnd(
        GameStats? game,
        bool isRecovered,
        string? sessionKey,
        string? champSelectPosition,
        string? champSelectMapJson)
    {
        if (game is null || isRecovered || IsFilled(game)) return false;

        var applied = false;
        if (!string.IsNullOrEmpty(sessionKey))
        {
            applied |= ApplyChampSelect(game, champSelectPosition, champSelectMapJson);
        }
        applied |= ApplyRolePriors(game);
        return applied;
    }

    /// <summary>
    /// The champ-select snapshot (<c>LcuClient.GetChampSelectSnapshotAsync</c>):
    /// the player's <c>assignedPosition</c> and a role→champion map whose own side
    /// is the lobby's assignment and whose enemy side is the role-prior estimate.
    /// Refused when the map does not hold the player's champion on the own side:
    /// that is another lobby's snapshot (a dodge, a remake, an app started
    /// mid-game). A lane the row already holds decides the opponent, and the row's
    /// own map is read before the snapshot's. Returns true when anything landed.
    /// </summary>
    public static bool ApplyChampSelect(GameStats? game, string? myPosition, string? participantMapJson)
    {
        if (game is null) return false;
        var snapshot = ParseMap(participantMapJson);
        if (snapshot is null || snapshot.Count == 0) return false;
        if (!OwnSideHolds(snapshot, game.ChampionName)) return false;

        var position = LiveRoster.NormalizePosition(myPosition);
        if (position.Length == 0) position = PositionFromOwnSlot(snapshot, game.ChampionName);

        var applied = false;
        if (game.Position.Length == 0 && position.Length > 0)
        {
            game.Position = position;
            applied = true;
        }
        if (game.ParticipantMap.Length == 0)
        {
            game.ParticipantMap = JsonSerializer.Serialize(snapshot);
            applied = true;
        }
        if (game.EnemyLaner.Length == 0)
        {
            var enemy = FirstNonEmpty(
                EnemyAt(ParseMap(game.ParticipantMap), game.Position),
                EnemyAt(snapshot, game.Position));
            if (enemy.Length > 0)
            {
                game.EnemyLaner = enemy;
                applied = true;
            }
        }

        if (applied) MarkEstimate(game, MatchupSources.ChampSelect);
        return applied;
    }

    /// <summary>
    /// Both sides estimated from champion role priors (<see cref="RoleAssignment"/>)
    /// over the champion lists the capture stored in <c>RawStats</c>
    /// (<c>_own_champions</c> / <c>_enemy_champions</c>). Summoner's Rift 5v5
    /// only: a comp of five per side in a CLASSIC game; anything else (ARAM,
    /// a custom, a short list) is refused rather than guessed. The player's lane
    /// comes from the row when it has one; the priors only fill a blank one.
    /// Returns true when anything landed.
    /// </summary>
    public static bool ApplyRolePriors(GameStats? game)
    {
        if (game is null) return false;
        if (!IsSummonersRiftFiveVsFive(game, out var own, out var enemy)) return false;

        var ownByRole = RoleAssignment.AssignRoles(own);
        var enemyByRole = RoleAssignment.AssignRoles(enemy);

        var estimate = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < RoleAssignment.RoleCount; i++)
        {
            if (ownByRole[i].Length > 0) estimate["own" + KeySuffixes[i]] = ownByRole[i];
            if (enemyByRole[i].Length > 0) estimate["enemy" + KeySuffixes[i]] = enemyByRole[i];
        }

        var applied = false;
        if (game.Position.Length == 0)
        {
            var myIndex = Array.FindIndex(ownByRole, c => SameChampion(c, game.ChampionName));
            if (myIndex >= 0)
            {
                game.Position = Positions[myIndex];
                applied = true;
            }
        }
        if (game.ParticipantMap.Length == 0 && estimate.Count > 0)
        {
            game.ParticipantMap = JsonSerializer.Serialize(estimate);
            applied = true;
        }
        if (game.EnemyLaner.Length == 0)
        {
            var enemyLaner = FirstNonEmpty(
                EnemyAt(ParseMap(game.ParticipantMap), game.Position),
                EnemyAt(estimate, game.Position));
            if (enemyLaner.Length > 0)
            {
                game.EnemyLaner = enemyLaner;
                applied = true;
            }
        }

        if (applied) MarkEstimate(game, MatchupSources.Heuristic);
        return applied;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An estimate landed on the row: mark it so the Match-V5 pass confirms it, even
    /// when the capture had stamped a confirmed source for the fields it resolved.
    /// The player's own word is never relabelled.
    /// </summary>
    private static void MarkEstimate(GameStats game, string source)
    {
        if (game.MatchupSource != MatchupSources.User) game.MatchupSource = source;
    }

    private static bool IsFilled(GameStats game) =>
        game.Position.Length > 0 && game.EnemyLaner.Length > 0 && game.ParticipantMap.Length > 0;

    private static string FirstNonEmpty(string first, string second) => first.Length > 0 ? first : second;

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

    private static string EnemyAt(Dictionary<string, string>? map, string? position)
    {
        if (map is null) return "";
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
