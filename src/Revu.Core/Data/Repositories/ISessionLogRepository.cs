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
    /// v2.18 (schema v6): <paramref name="pregameIntention"/> /
    /// <paramref name="intentionSource"/> carry the champ-select intent; on
    /// update, default (empty / 0) values never overwrite an existing stamp —
    /// the review-save re-log must not clobber what EOG wrote.
    /// </summary>
    Task LogGameAsync(
        long gameId,
        string championName,
        bool win,
        int mentalRating = 5,
        string improvementNote = "",
        int preGameMood = 0,
        bool ruleBroken = false,
        string pregameIntention = "",
        string intentionSource = ""
    );

    /// <summary>Update the mental rating for a specific game.</summary>
    Task UpdateMentalRatingAsync(long gameId, int mentalRating);

    /// <summary>Mark a game as skip-reviewed — the user cleared it from the
    /// queue without supplying a mental rating or notes. Skipped games are
    /// excluded from AvgMental / mental-trend / tilt-warning queries so a
    /// one-click skip doesn't pollute behavioral signal.</summary>
    Task MarkSkippedAsync(long gameId);

    /// <summary>Reset is_skipped to 0 — used when a previously-skipped game
    /// gets a real review on a follow-up open of the review page.</summary>
    Task ClearSkippedAsync(long gameId);

    /// <summary>Delete-review: clear ONLY the three fields that gate the review
    /// queue (improvement_note, mental_handled, is_skipped), returning the game to
    /// the unreviewed queue. Deliberately preserves mental_rating, focus_adherence
    /// and rule_broken so the live-computed mental + adherence streaks (and the
    /// pre-2026-06-12 grandfathered verdicts) are untouched — the row stays.</summary>
    Task ClearReviewMarkersAsync(long gameId);

    /// <summary>v2.15.10: clear or set the rule_broken flag for a specific game.
    /// User-initiated only — used to undo a false positive flagged by the
    /// since-removed heuristic, or by the live rules engine.
    /// v2.16: clearing also writes a sticky stamp in cleared_rule_breaks so the
    /// flag stays cleared if the rules engine re-evaluates the same game later.
    /// Re-flagging removes the sticky stamp.</summary>
    Task SetRuleBrokenAsync(long gameId, bool ruleBroken);

    /// <summary>v2.16: returns true if the user explicitly cleared the
    /// rule_broken flag for this game. Callers should suppress any
    /// auto-flagging when this returns true.</summary>
    Task<bool> IsRuleBreakClearedAsync(long gameId);

    /// <summary>Save the post-game mental reflection for a specific game.</summary>
    Task UpdateMentalHandledAsync(long gameId, string mentalHandled);

    /// <summary>Set or update the session intention for a given date.</summary>
    Task SetSessionIntentionAsync(string dateStr, string intention);

    /// <summary>Save the session debrief (did you stick to your goal?).</summary>
    Task SaveSessionDebriefAsync(string dateStr, int rating, string note = "");

    /// <summary>
    /// v2.18 (schema v5): stamp the one-tap focus-adherence answer
    /// (null = unanswered, 0 = no, 1 = partly, 2 = yes) on a game's row.
    /// </summary>
    Task UpdateFocusAdherenceAsync(long gameId, int? adherence);

    /// <summary>Get a single session_log entry by game_id, or null if not found.</summary>
    Task<SessionLogEntry?> GetEntryAsync(long gameId);

    /// <summary>Get session log entries for a specific date (YYYY-MM-DD).</summary>
    Task<List<SessionLogEntry>> GetForDateAsync(string dateStr);

    /// <summary>Get today's session log entries ordered chronologically.</summary>
    Task<List<SessionLogEntry>> GetTodayAsync();

    /// <summary>Get the full session record for a date, or null.</summary>
    Task<SessionInfo?> GetSessionAsync(string dateStr);

    /// <summary>
    /// The most recent OPEN block on or after <paramref name="minDate"/>: a session
    /// with an intention set but no debrief recorded yet (started but never ended).
    /// Null when there's no such recent open block. minDate bounds the carry-over so
    /// only a just-missed block (e.g. yesterday's) is offered for End Block — older
    /// orphaned blocks are treated as abandoned and never reclaim the dashboard.
    /// </summary>
    Task<SessionInfo?> GetOpenBlockAsync(string minDate);

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

    /// <summary>
    /// Bulk-fetch all (game_id → mental_rating) pairs from session_log in a single
    /// query. Used by AnalysisService to avoid one-per-game N+1 round-trips when
    /// building a filtered profile. Only rows where is_skipped = 0 are included.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> GetAllMentalRatingsAsync();
}
