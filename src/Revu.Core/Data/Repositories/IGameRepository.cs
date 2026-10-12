#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

// ── Result record types ──────────────────────────────────────────────

/// <summary>Focus/mistakes/went-well from the most recently reviewed game.
/// v2.18 (schema v6 build): champion/result/timestamp let the pre-game intent
/// card show where its carried default came from and how old it is.</summary>
public sealed record ReviewFocus(
    string FocusNext,
    string Mistakes,
    string WentWell,
    string ChampionName = "",
    bool Win = false,
    long Timestamp = 0
);

/// <summary>A game's spotted-problems entry for the problems list.</summary>
public sealed record SpottedProblem(
    long GameId,
    string ChampionName,
    string SpottedProblems,
    string DatePlayed,
    bool Win,
    string EnemyChampion = ""
);

// ── Interface ────────────────────────────────────────────────────────

/// <summary>Writes game rows and review metadata.</summary>
public interface IGameWriter
{
    /// <summary>
    /// Save game stats to the database. Returns the row id.
    /// Casual modes (ARAM, Arena, etc.) are silently skipped (returns -1).
    /// </summary>
    Task<int> SaveAsync(GameStats stats, DbWriteSession? session = null);

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
        List<string>? tags = null,
        DbWriteSession? session = null
    );

    /// <summary>Update the review fields for a game (by game_id, not row id).</summary>
    Task UpdateReviewAsync(long gameId, GameReview review, DbWriteSession? session = null);

    /// <summary>Update the enemy_laner field for a game.</summary>
    Task UpdateEnemyLanerAsync(long gameId, string enemyLaner, DbWriteSession? session = null);

    /// <summary>
    /// v2.16: write the full 10-participant role-to-champion JSON map. Empty
    /// string clears it. Persisted alongside enemy_laner during the same
    /// Match-V5 round-trip so role-aware matchup display can render pairings
    /// like "Kai'Sa+Nautilus vs Tristana+Renata".
    /// </summary>
    Task UpdateParticipantMapAsync(long gameId, string participantMapJson);

    /// <summary>
    /// v3.10.1 (schema v17): the Match-V5 write — enemy_laner, participant_map and
    /// the player's position in one statement, each kept when the new value is
    /// blank, plus the <c>matchup_source</c> stamp (<c>MatchupSources.MatchV5</c>)
    /// that takes the row out of the confirmation queue. A row whose opponent the
    /// player typed (<c>MatchupSources.User</c>) keeps that opponent and its stamp;
    /// only the map and the position fill in.
    /// </summary>
    Task UpdateMatchupAsync(long gameId, string enemyLaner, string participantMapJson, string position, string source);

    /// <summary>
    /// v2.18 (schema v5): persist laning-at-10 numbers from the Match-V5
    /// timeline backfill. Diffs are null when the lane opponent couldn't be
    /// identified.
    /// </summary>
    Task UpdateLaningAt10Async(long gameId, double csAt10, int? goldDiffAt10, double? csDiffAt10);

    /// <summary>v3.2 (schema v11): mark a game processed by the map-state pass
    /// at the given analyzer version.</summary>
    Task UpdateMapStateVersionAsync(long gameId, int version);

    /// <summary>Re-queue a game for the map-state pass (map_state_v back to NULL):
    /// a capture-time re-save wipes the derived rows and death stamps, so the next
    /// post-game pass or Settings backfill must derive them again.</summary>
    Task ClearMapStateVersionAsync(long gameId);

    /// <summary>v3.5 (schema v13): game_ids in the pattern window not yet
    /// processed by the pattern-evidence materializer at the given version.
    /// Excludes hidden + casual games; newest first.</summary>
    Task<IReadOnlyList<long>> GetPatternEvidenceBackfillIdsAsync(
        int currentVersion, int windowDays = Constants.PatternConstants.WindowDays);

    /// <summary>v3.5 (schema v13): mark a game processed by the pattern-evidence pass.</summary>
    Task UpdatePatternEvidenceVersionAsync(long gameId, int version);

    /// <summary>
    /// 3.14.1: record a remake verdict (game_remakes). Applies only when the source ranks
    /// at least as high as the stored one (user > matchv5 > eog = lcu), so the player's own
    /// verdict is sticky. Changed is true only when the effective is_remake flipped.
    /// </summary>
    Task<RemakeWriteResult> SetRemakeVerdictAsync(long gameId, bool isRemake, bool causedBySelf, string source, DbWriteSession? session = null);
}

/// <summary>Read model for game history and review queues.</summary>
public interface IGameHistoryQuery
{
    /// <summary>v2.15.8: enumerate game_ids that have no enemy_laner set, so a
    /// backfill pass can resolve them via the Riot API. Excludes hidden games.</summary>
    Task<IReadOnlyList<long>> GetGameIdsMissingEnemyLanerAsync();

    /// <summary>v2.18 (schema v5): game_ids with no laning-at-10 numbers yet,
    /// for the Match-V5 timeline backfill. Excludes hidden + casual games.</summary>
    Task<IReadOnlyList<long>> GetGameIdsMissingLaningAsync();

    /// <summary>v3.2 (schema v11): game_ids not yet processed by the map-state
    /// pass at the given analyzer version. Scoped to the review queue — only
    /// UNREVIEWED games from the last <paramref name="recentDays"/> days (the
    /// markers anchor upcoming reviews; a full-history walk is deliberately not
    /// offered). Excludes hidden + casual games.</summary>
    Task<IReadOnlyList<long>> GetGameIdsMissingMapStateAsync(int currentVersion, int recentDays = 14);

    /// <summary>Get a single game by game_id, or null if not found.</summary>
    Task<GameStats?> GetAsync(long gameId);

    /// <summary>Get recent ranked/normal games ordered by most recent first.</summary>
    Task<List<GameStats>> GetRecentAsync(
        int limit = 50,
        int offset = 0,
        string? champion = null,
        bool? win = null);

    /// <summary>Count recent ranked/normal games for the given history filters.</summary>
    Task<int> GetRecentCountAsync(string? champion = null, bool? win = null);

    /// <summary>
    /// Count ranked/normal games that have persisted review signal in the game
    /// row, session log, or concept tags.
    /// </summary>
    Task<int> GetReviewedCountAsync();

    /// <summary>Get ranked/normal games played today.</summary>
    Task<List<GameStats>> GetTodaysGamesAsync();

    /// <summary>
    /// Get recent ranked/normal games that haven't been reviewed yet.
    /// A game counts as 'reviewed' when it has any meaningful post-game review
    /// content saved in the game row, related session_log row, or concept tags.
    /// </summary>
    Task<List<GameStats>> GetUnreviewedGamesAsync(int days = 3);

    /// <summary>Get the focus_next and mistakes from the most recent reviewed game.</summary>
    Task<ReviewFocus?> GetLastReviewFocusAsync(string? currentPuuid = null);

    /// <summary>3.14.1: the stored remake verdict, or null when none was recorded.</summary>
    Task<RemakeVerdict?> GetRemakeVerdictAsync(long gameId);

    /// <summary>3.14.1: visible Ranked Solo/Duo games with a known duration shorter than
    /// <paramref name="maxDurationS"/> and no verdict yet, newest first (the startup remake
    /// check's work list). Manual entries are never looked up: their id is a timestamp.</summary>
    Task<List<RemakeCandidate>> GetRemakeCandidatesAsync(int maxDurationS, int limit);

    /// <summary>3.14.1: today's games as rule input (the <see cref="GetTodaysGamesAsync"/>
    /// predicate minus remakes). Every live rule check reads this.</summary>
    Task<List<RuleCheckGame>> GetTodaysRuleGamesAsync();
}

/// <summary>Read model for the spotted problems that seed objective suggestions.</summary>
public interface IGameAnalyticsQuery
{
    /// <summary>Get recent games that have spotted_problems notes (excludes casual modes and remakes).</summary>
    Task<List<SpottedProblem>> GetRecentSpottedProblemsAsync(int limit = 20, string? currentPuuid = null);
}

/// <summary>Destructive and visibility-changing game operations.</summary>
public interface IGameDeletionService
{
    /// <summary>Soft-delete (or restore) a game. Hidden games are excluded from all views.</summary>
    Task SetHiddenAsync(long gameId, bool hidden);

    /// <summary>
    /// Permanently remove a game and all rows in child tables that reference its
    /// <c>game_id</c>. Wrapped in a single transaction so a partial failure rolls
    /// back the entire delete. Backup of the DB file is snapshotted BEFORE any
    /// mutation happens.
    /// </summary>
    /// <returns>
    /// Path to the backup file the service created before deleting. Callers
    /// should surface this to the user (the delete is irreversible inside the
    /// app, but the backup gives them a way out).
    /// </returns>
    Task<string> DeleteAsync(long gameId);
}

/// <summary>
/// Compatibility facade for game stats CRUD and read models.
/// Prefer the focused interfaces above for new consumers.
/// </summary>
public interface IGameRepository :
    IGameWriter,
    IGameHistoryQuery,
    IGameAnalyticsQuery,
    IGameDeletionService
{
}
