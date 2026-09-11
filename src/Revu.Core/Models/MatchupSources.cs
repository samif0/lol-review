#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// v3.10.1 (schema v17): where a game row's matchup columns (position /
/// enemy_laner / participant_map) came from, stored in <c>games.matchup_source</c>.
///
/// <para>The matchup must be on the row the moment the game ends, not after a
/// Match-V5 round-trip, so the capture path resolves it from the best source
/// it has at end-of-game and stamps which one. Confirmed sources carry the
/// matchmaker's own assignment; the estimated ones are good enough to show
/// immediately but stay in the Match-V5 backfill queue
/// (<c>IGameRepository.GetGameIdsMissingEnemyLanerAsync</c>) until Riot's
/// <c>teamPosition</c> confirms or corrects them.</para>
/// </summary>
public static class MatchupSources
{
    /// <summary>The LCU end-of-game payload carried per-player positions.</summary>
    public const string Eog = "eog";

    /// <summary>The Live Client Data <c>playerlist</c> captured during the game (the game's own assignment).</summary>
    public const string Live = "live";

    /// <summary>Own side + position from champ select; enemy side estimated from champion role priors.</summary>
    public const string ChampSelect = "champselect";

    /// <summary>Both sides estimated from champion role priors over the end-of-game champion lists.</summary>
    public const string Heuristic = "heuristic";

    /// <summary>The LCU match history lane/role heuristic (a game recovered after the fact).</summary>
    public const string History = "history";

    /// <summary>Riot Match-V5 <c>teamPosition</c> — the authoritative post-game record.</summary>
    public const string MatchV5 = "matchv5";

    /// <summary>The player typed the opponent on the Review page; automation never overwrites it.</summary>
    public const string User = "user";

    /// <summary>The matchmaker's own assignment (or the player's word): nothing left to confirm.</summary>
    public static bool IsConfirmed(string? source) =>
        source is Eog or Live or MatchV5 or User;

    /// <summary>An estimate shown immediately that the Match-V5 pass should confirm or correct.</summary>
    public static bool NeedsConfirmation(string? source) =>
        source is ChampSelect or Heuristic or History;
}
