#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Data.Repositories;

// ── Result record types ──────────────────────────────────────────────

/// <summary>Aggregate stats for a single champion.</summary>
public sealed record ChampionStats(
    string ChampionName,
    int GamesPlayed,
    int Wins,
    double Winrate,
    double AvgKills,
    double AvgDeaths,
    double AvgAssists,
    double AvgKda,
    double AvgCsMin,
    double AvgVision,
    double AvgDamage
);

/// <summary>Overall aggregate stats across all ranked/normal games.</summary>
public sealed record OverallStats(
    int TotalGames,
    int TotalWins,
    double Winrate,
    double AvgKills,
    double AvgDeaths,
    double AvgAssists,
    double AvgKda,
    double AvgCsMin,
    double AvgVision,
    int TotalPentas,
    int TotalQuadras,
    int MaxKills,
    double BestKda
);

/// <summary>Focus/mistakes/went-well from the most recently reviewed game.</summary>
public sealed record ReviewFocus(
    string FocusNext,
    string Mistakes,
    string WentWell
);

/// <summary>Win/loss breakdown by attribution value.</summary>
public sealed record AttributionStat(
    string Attribution,
    int Games,
    int Wins,
    int Losses,
    double Winrate
);

/// <summary>A game's spotted-problems entry for the problems list.</summary>
public sealed record SpottedProblem(
    long GameId,
    string ChampionName,
    string SpottedProblems,
    string DatePlayed,
    bool Win
);

/// <summary>Minimal game data for trend charts.</summary>
public sealed record ChartDataPoint(
    long GameId,
    bool Win,
    int Deaths,
    long Timestamp,
    string ChampionName,
    double KdaRatio
);

/// <summary>Matchup winrate: champion vs enemy laner.</summary>
public sealed record MatchupStat(
    string ChampionName,
    string EnemyLaner,
    int Games,
    int Wins,
    double Winrate,
    double AvgKda,
    double AvgDeaths
);

/// <summary>Per-game performance metrics for trend charting.</summary>
public sealed record PerformanceTrend(
    double CsPerMin,
    int VisionScore,
    double KdaRatio,
    int Deaths,
    double KillParticipation,
    string ChampionName,
    long Timestamp,
    bool Win
);

/// <summary>Performance stats grouped by role/position.</summary>
public sealed record RoleStat(
    string Position,
    int Games,
    int Wins,
    double Winrate,
    double AvgKda
);

/// <summary>Win rates bucketed by game duration.</summary>
public sealed record DurationStat(
    string Bucket,
    int Games,
    int Wins,
    double Winrate
);

/// <summary>Aggregate stats over the last N games for suggestion thresholds.</summary>
public sealed record RecentStats(
    int Games,
    double Winrate,
    double AvgCsMin,
    double AvgVision,
    double AvgDeaths,
    double AvgKda,
    double AvgKills
);

// ── Interface ────────────────────────────────────────────────────────

/// <summary>
/// Repository for game stats CRUD — ported from Python GameRepository.
/// </summary>
public interface IGameRepository
{
    /// <summary>
    /// Save game stats to the database. Returns the row id.
    /// Casual modes (ARAM, Arena, etc.) are silently skipped (returns -1).
    /// </summary>
    Task<int> SaveAsync(GameStats stats);

    /// <summary>
    /// Save a manually entered game with minimal required fields.
    /// Returns the generated game_id (unix timestamp).
    /// </summary>
    Task<int> SaveManualAsync(
        string championName,
        bool win,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        string gameMode = "Manual Entry",
        string notes = "",
        string mistakes = "",
        string wentWell = "",
        string focusNext = "",
        List<string>? tags = null
    );

    /// <summary>Update the review fields for a game (by game_id, not row id).</summary>
    Task UpdateReviewAsync(long gameId, GameReview review);

    /// <summary>Update the enemy_laner field for a game.</summary>
    Task UpdateEnemyLanerAsync(long gameId, string enemyLaner);

    /// <summary>Get a single game by game_id, or null if not found.</summary>
    Task<GameStats?> GetAsync(long gameId);

    /// <summary>Get recent ranked/normal games ordered by most recent first.</summary>
    Task<List<GameStats>> GetRecentAsync(int limit = 50, int offset = 0);

    /// <summary>Get ranked/normal games played on a specific date (YYYY-MM-DD).</summary>
    Task<List<GameStats>> GetGamesForDateAsync(string dateStr);

    /// <summary>Get ranked/normal games played today.</summary>
    Task<List<GameStats>> GetTodaysGamesAsync();

    /// <summary>Get all losses, optionally filtered by champion (excludes casual modes).</summary>
    Task<List<GameStats>> GetLossesAsync(string? champion = null);

    /// <summary>
    /// Get recent ranked/normal games that haven't been reviewed yet.
    /// A game counts as 'unreviewed' if it has no rating, no mistakes text,
    /// no went_well text, and no focus_next text.
    /// </summary>
    Task<List<GameStats>> GetUnreviewedGamesAsync(int days = 3);

    /// <summary>Get list of unique champion names from ranked/normal games.</summary>
    Task<List<string>> GetUniqueChampionsAsync(bool lossesOnly = false);

    /// <summary>Aggregate stats grouped by champion (excludes casual modes).</summary>
    Task<List<ChampionStats>> GetChampionStatsAsync();

    /// <summary>Get aggregate stats across all ranked/normal games.</summary>
    Task<OverallStats> GetOverallStatsAsync();

    /// <summary>Get the focus_next and mistakes from the most recent reviewed game.</summary>
    Task<ReviewFocus?> GetLastReviewFocusAsync();

    /// <summary>
    /// Get current win/loss streak. Positive = wins, negative = losses.
    /// </summary>
    Task<int> GetWinStreakAsync();

    /// <summary>Get win/loss breakdown by attribution value.</summary>
    Task<List<AttributionStat>> GetAttributionStatsAsync();

    /// <summary>Get recent games that have spotted_problems notes.</summary>
    Task<List<SpottedProblem>> GetRecentSpottedProblemsAsync(int limit = 20);

    /// <summary>Get recent game data for trend charts (returned in chronological order).</summary>
    Task<List<ChartDataPoint>> GetRecentForChartsAsync(int limit = 100);

    /// <summary>Get win rates grouped by champion vs enemy laner matchup.</summary>
    Task<List<MatchupStat>> GetMatchupStatsAsync();

    /// <summary>Get per-game performance metrics for trend charting (chronological).</summary>
    Task<List<PerformanceTrend>> GetPerformanceTrendsAsync(int limit = 50);

    /// <summary>Get performance stats grouped by role/position.</summary>
    Task<List<RoleStat>> GetRoleStatsAsync();

    /// <summary>Get win rates bucketed by game duration.</summary>
    Task<List<DurationStat>> GetDurationStatsAsync();

    /// <summary>Get aggregate stats over the last N games for suggestion thresholds.</summary>
    Task<RecentStats> GetRecentStatsAsync(int limit = 20);
}
