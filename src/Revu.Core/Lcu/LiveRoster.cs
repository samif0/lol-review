#nullable enable

using System.Text.Json;

namespace Revu.Core.Lcu;

/// <summary>One row of the Live Client Data <c>/liveclientdata/playerlist</c> snapshot.</summary>
/// <param name="ChampionName">Display name as the game reports it ("Miss Fortune", "Kai'Sa").</param>
/// <param name="Position">TOP / JUNGLE / MIDDLE / BOTTOM / UTILITY, or "" in non-positional queues.</param>
/// <param name="TeamId">100 (ORDER) or 200 (CHAOS); 0 when the payload carried neither.</param>
public sealed record LiveRosterPlayer(
    string ChampionName,
    string Position,
    int TeamId,
    string RiotIdGameName,
    string SummonerName);

/// <summary>
/// v3.10.1: the ten players of the running game as the game itself reports them,
/// captured by <see cref="LiveEventCollector"/> from <c>/liveclientdata/playerlist</c>.
///
/// <para>Why it exists: since 2026-08-14 the LCU end-of-game payload has carried
/// no per-player position at all (<c>selectedPosition</c> and
/// <c>detectedTeamPosition</c> both blank), so the matchup — the player's lane,
/// their lane opponent, the role→champion map — could only be recovered from
/// Match-V5 minutes later. The live client's <c>position</c> is the matchmaker's
/// assignment (the same thing Match-V5 later reports as <c>teamPosition</c>) and
/// is available for the whole game, so the matchup can be on the row the moment
/// the game ends.</para>
/// </summary>
public sealed class LiveRoster
{
    public IReadOnlyList<LiveRosterPlayer> Players { get; }

    public LiveRoster(IReadOnlyList<LiveRosterPlayer> players)
    {
        Players = players;
    }

    /// <summary>A full Summoner's Rift lobby, every champion named. The collector stops refetching once this holds.</summary>
    public bool IsComplete =>
        Players.Count >= 10 && Players.All(p => p.ChampionName.Length > 0);

    /// <summary>At least one player carries a lane; false for ARAM / practice tool.</summary>
    public bool HasPositions => Players.Any(p => p.Position.Length > 0);

    /// <summary>
    /// Parse the <c>playerlist</c> array. Null when the payload is not an array;
    /// otherwise every entry with a champion name, positions normalized to the
    /// five lane values ("" when blank or a placeholder).
    /// </summary>
    public static LiveRoster? Parse(JsonElement playerList)
    {
        if (playerList.ValueKind != JsonValueKind.Array) return null;

        var players = new List<LiveRosterPlayer>();
        foreach (var p in playerList.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var champion = (p.GetPropertyOrDefault("championName", "") ?? "").Trim();
            if (champion.Length == 0) continue;

            players.Add(new LiveRosterPlayer(
                champion,
                NormalizePosition(p.GetPropertyOrDefault("position", "")),
                TeamIdOf(p.GetPropertyOrDefault("team", "")),
                (p.GetPropertyOrDefault("riotIdGameName", "") ?? "").Trim(),
                (p.GetPropertyOrDefault("summonerName", "") ?? "").Trim()));
        }
        return new LiveRoster(players);
    }

    /// <summary>
    /// The lane the game assigned to <paramref name="championName"/> on
    /// <paramref name="teamId"/> (100 / 200; 0 = either team). Names compare by
    /// letters and digits only, so "Kai'Sa" / "Kaisa" / "KaiSa" all match. Empty
    /// when the player is not in the roster or carries no lane.
    /// </summary>
    public string PositionOf(string championName, int teamId)
    {
        var key = ChampionKey(championName);
        if (key.Length == 0) return "";
        foreach (var p in Players)
        {
            if (teamId != 0 && p.TeamId != 0 && p.TeamId != teamId) continue;
            if (ChampionKey(p.ChampionName) == key) return p.Position;
        }
        return "";
    }

    internal static int TeamIdOf(string? team) => (team ?? "").Trim().ToUpperInvariant() switch
    {
        "ORDER" => 100,
        "CHAOS" => 200,
        _ => 0,
    };

    // Only the five real lane values count; the live client emits "" (and some
    // builds "NONE") outside positional queues.
    internal static string NormalizePosition(string? value)
    {
        var upper = (value ?? "").Trim().ToUpperInvariant();
        return upper switch
        {
            "TOP" or "JUNGLE" or "MIDDLE" or "BOTTOM" or "UTILITY" => upper,
            "SUPPORT" => "UTILITY",
            _ => "",
        };
    }

    /// <summary>Letters and digits only, lower-cased: the loosest champion-name equality that is still exact.</summary>
    public static string ChampionKey(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        Span<char> buf = stackalloc char[name.Length];
        var n = 0;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        }
        return new string(buf[..n]);
    }
}
