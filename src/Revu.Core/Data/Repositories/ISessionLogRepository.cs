#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

// ── Result record types ──────────────────────────────────────────────

/// <summary>Aggregate session stats for a single date.</summary>
public sealed record SessionDayStats(
    int Games,
    int Wins,
    int Losses,
    double AvgMental,
    int RuleBreaks
);

/// <summary>Per-day summary stats (for the daily summaries view).</summary>
public sealed record DailySummary(
    string Date,
    int Games,
    int Wins,
    int Losses,
    double AvgMental,
    int RuleBreaks,
    string ChampionsPlayed
);

/// <summary>Winrate by mental rating bracket.</summary>
public sealed record MentalCorrelationPoint(
    string Bracket,
    int Games,
    int Wins,
    double Winrate
);

/// <summary>A single mental rating data point for trend charting.</summary>
public sealed record MentalTrendPoint(
    long Timestamp,
    int MentalRating,
    bool Win,
    string ChampionName
);

/// <summary>Winrate by pre-game mood level (1-5).</summary>
public sealed record MoodCorrelationPoint(
    int Mood,
    int Games,
    int Wins,
    double Winrate
);

/// <summary>Tilt warning when mental drops sharply between games.</summary>
public sealed record TiltWarning(
    int FromMental,
    int ToMental,
    string GameChampion,
    long? GameId
);

/// <summary>Aggregate session-level patterns for player profiling.</summary>
public sealed record SessionPatterns(
    double AvgGamesPerSession,
    double AvgMentalDelta,
    double TiltFrequencyPct,
    int TotalSessionDays
);

// ── Interface ────────────────────────────────────────────────────────

/// <summary>
/// Repository for session log CRUD — ported from Python SessionLogRepository.
/// </summary>
public interface ISessionLogRepository
{
    /// <summary>
    /// Log a game in the session log with mental rating and notes.
    /// If an entry already exists for this game_id, updates it instead.
    /// <paramref name="ruleBroken"/> should only be true when a user-defined rule
    /// was violated — it is never auto-detected by the repository.
    /// </summary>
    Task LogGameAsync(
        long gameId,
        string championName,
        bool win,
        int mentalRating = 5,
        string improvementNote = "",
        int preGameMood = 0,
        bool ruleBroken = false
    );

    /// <summary>Update the mental rating for a specific game.</summary>
    Task UpdateMentalRatingAsync(long gameId, int mentalRating);

    /// <summary>v2.15.10: clear or set the rule_broken flag for a specific game.
    /// User-initiated only — used to undo a false positive flagged by the
    /// since-removed heuristic, or by the live rules engine.</summary>
    Task SetRuleBrokenAsync(long gameId, bool ruleBroken);

    /// <summary>Save the post-game mental reflection for a specific game.</summary>
    Task UpdateMentalHandledAsync(long gameId, string mentalHandled);

    /// <summary>Set or update the session intention for a given date.</summary>
    Task SetSessionIntentionAsync(string dateStr, string intention);

    /// <summary>Save the session debrief (did you stick to your goal?).</summary>
    Task SaveSessionDebriefAsync(string dateStr, int rating, string note = "");

    /// <summary>Get a single session_log entry by game_id, or null if not found.</summary>
    Task<SessionLogEntry?> GetEntryAsync(long gameId);

    /// <summary>Get session log entries for a specific date (YYYY-MM-DD).</summary>
    Task<List<SessionLogEntry>> GetForDateAsync(string dateStr);

    /// <summary>Get today's session log entries ordered chronologically.</summary>
    Task<List<SessionLogEntry>> GetTodayAsync();

    /// <summary>Get the full session record for a date, or null.</summary>
    Task<SessionInfo?> GetSessionAsync(string dateStr);

    /// <summary>Check if a game already has a session_log entry.</summary>
    Task<bool> HasEntryAsync(long gameId);

    /// <summary>
    /// Remove session_log entries whose date doesn't match the game's actual date.
    /// Returns the number of deleted rows.
    /// </summary>
    Task<int> CleanupMismatchedEntriesAsync();

    /// <summary>Get aggregate session stats for today.</summary>
    Task<SessionDayStats> GetStatsTodayAsync();

    /// <summary>Get aggregate session stats for a specific date.</summary>
    Task<SessionDayStats> GetStatsForDateAsync(string dateStr);

    /// <summary>Get all dates that have session log entries, newest first.</summary>
    Task<List<string>> GetDatesWithGamesAsync();

    /// <summary>Get session log entries for the last N days.</summary>
    Task<List<SessionLogEntry>> GetRangeAsync(int days = 7);

    /// <summary>Get per-day summary stats for the last N days.</summary>
    Task<List<DailySummary>> GetDailySummariesAsync(int days = 7);

    /// <summary>Count consecutive clean play-days (no rule breaks).</summary>
    Task<int> GetAdherenceStreakAsync();

    /// <summary>Analyze winrate by mental rating bracket.</summary>
    Task<List<MentalCorrelationPoint>> GetMentalWinrateCorrelationAsync();

    /// <summary>Get recent mental ratings for trend charting (chronological).</summary>
    Task<List<MentalTrendPoint>> GetMentalTrendAsync(int limit = 50);

    /// <summary>Analyze winrate by pre-game mood level (1-5).</summary>
    Task<List<MoodCorrelationPoint>> GetMoodWinrateCorrelationAsync();

    /// <summary>
    /// Check for mental rating drops between consecutive games today.
    /// Returns a warning if mental dropped by >= 3 between adjacent games,
    /// or null if no tilt detected.
    /// </summary>
    Task<TiltWarning?> CheckTiltWarningAsync(string dateStr);

    /// <summary>Get aggregate session-level patterns for player profiling.</summary>
    Task<SessionPatterns> GetSessionPatternsAsync();
}
