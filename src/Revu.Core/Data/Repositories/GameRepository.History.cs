#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    // v3.3 (schema v12): a game played on a with-coach block day counts as
    // reviewed — the coach reviews those games outside Revu. The date mapping
    // is the same canonical CASE as ResolveGameSessionDateAsync (date_played
    // first, timestamp fallback) so the exemption works even for import paths
    // that never wrote a session_log
    // row. Two legs: (1) a with-coach block on the game's own day; (2) a
    // with-coach block on the PREVIOUS day that was still open past midnight
    // (ended_at NULL = carried over, or closed on/after the game's day) — the
    // 23:00 coach session whose games finish at 01:30 must stay exempt, same
    // cross-midnight model as the dashboard's End Block carry-over.
    // Deliberately a sessions-level signal: session_log is untouched, so
    // mental stats, tilt warnings, and the adherence streak stay byte-identical
    // (never route this through is_skipped).
    private const string GameLocalDateCase = @"CASE
                                WHEN COALESCE(games.date_played, '') != '' THEN SUBSTR(games.date_played, 1, 10)
                                WHEN COALESCE(games.timestamp, 0) > 0 THEN DATE(games.timestamp, 'unixepoch', 'localtime')
                                ELSE ''
                              END";

    private const string ReviewedByCoachBlockTerm = @"
                 EXISTS (
                        SELECT 1
                        FROM sessions
                        WHERE COALESCE(sessions.with_coach, 0) = 1
                          AND (
                                sessions.date = " + GameLocalDateCase + @"
                             OR (
                                    sessions.date = DATE(" + GameLocalDateCase + @", '-1 day')
                                AND (
                                        sessions.ended_at IS NULL
                                     OR DATE(sessions.ended_at, 'unixepoch', 'localtime') >= " + GameLocalDateCase + @"
                                    )
                                )
                              )
                    )";

    public async Task<IReadOnlyList<long>> GetGameIdsMissingEnemyLanerAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // v2.16: also pick up games that have enemy_laner but a blank
        // participant_map. Pre-v2.16 backfill only stored the lane opponent;
        // now we want the full 10-champion map for role-aware matchup pills,
        // so any row missing EITHER column needs a Match-V5 round-trip.
        // v3.10.1: and rows whose matchup is an ESTIMATE stamped at game end
        // (champ select / role priors / match-history heuristic) — shown at once,
        // confirmed or corrected by Match-V5 here. Legacy '' rows with both
        // columns filled are done (EOG positions or an earlier backfill).
        cmd.CommandText = $@"
            SELECT game_id FROM games
            WHERE (
                    (enemy_laner IS NULL OR enemy_laner = '')
                 OR (participant_map IS NULL OR participant_map = '')
                 OR COALESCE(matchup_source, '') IN (
                        '{MatchupSources.ChampSelect}', '{MatchupSources.Heuristic}', '{MatchupSources.History}')
                  )
              {CasualFilter}
              AND (is_hidden IS NULL OR is_hidden = 0)
            ORDER BY timestamp DESC";

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }


    public async Task<GameStats?> GetAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT games.*, {RemakeSql.IsRemakeColumn("games.game_id")} FROM games WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapGameStats(reader) : null;
    }

    // ── List reads ───────────────────────────────────────────────────────

    public async Task<List<GameStats>> GetRecentAsync(
        int limit = 50,
        int offset = 0,
        string? champion = null,
        bool? win = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var whereClauses = new List<string> { "1=1" };
        ApplyRecentFilters(cmd, whereClauses, champion, win);
        cmd.CommandText =
            $"SELECT games.*, {RemakeSql.IsRemakeColumn("games.game_id")} FROM games WHERE {string.Join(" AND ", whereClauses)} {CasualFilter} " +
            "ORDER BY timestamp DESC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<int> GetRecentCountAsync(string? champion = null, bool? win = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var whereClauses = new List<string> { "1=1" };
        ApplyRecentFilters(cmd, whereClauses, champion, win);
        cmd.CommandText = $"SELECT COUNT(*) FROM games WHERE {string.Join(" AND ", whereClauses)} {CasualFilter}";
        var result = await cmd.ExecuteScalarAsync();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    public async Task<int> GetReviewedCountAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COUNT(*)
            FROM games
            WHERE NOT (
                    COALESCE(rating, 0) <= 0
                AND COALESCE(review_notes, '') = ''
                AND COALESCE(mistakes, '') = ''
                AND COALESCE(went_well, '') = ''
                AND COALESCE(focus_next, '') = ''
                AND COALESCE(spotted_problems, '') = ''
                AND COALESCE(outside_control, '') = ''
                AND COALESCE(within_control, '') = ''
                AND COALESCE(attribution, '') = ''
                AND COALESCE(personal_contribution, '') = ''
                AND NOT EXISTS (
                    SELECT 1
                    FROM session_log
                    WHERE session_log.game_id = games.game_id
                      AND (
                            COALESCE(session_log.improvement_note, '') != ''
                         OR COALESCE(session_log.mental_handled, '') != ''
                         OR COALESCE(session_log.is_skipped, 0) = 1
                      )
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM game_concept_tags
                    WHERE game_concept_tags.game_id = games.game_id
                )
                AND NOT {ReviewedByCoachBlockTerm}
            )
            {CasualFilter}
            AND (is_hidden IS NULL OR is_hidden = 0)";

        var result = await cmd.ExecuteScalarAsync();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    public async Task<List<GameStats>> GetTodaysGamesAsync()
    {
        using var conn = _factory.CreateConnection();

        var todayStart = DateTime.Today;
        var todayTimestamp = new DateTimeOffset(todayStart).ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        // 3.14.1: today's list keeps remakes (labelled); rule input reads GetTodaysRuleGamesAsync.
        cmd.CommandText = $"SELECT games.*, {RemakeSql.IsRemakeColumn("games.game_id")} FROM games WHERE timestamp >= @ts {CasualFilter} ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@ts", todayTimestamp);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<GameStats>> GetUnreviewedGamesAsync(int days = 3)
    {
        using var conn = _factory.CreateConnection();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT * FROM games
            WHERE timestamp >= @cutoff
              AND NOT (
                    COALESCE(rating, 0) > 0
                 OR COALESCE(review_notes, '') != ''
                 OR COALESCE(mistakes, '') != ''
                 OR COALESCE(went_well, '') != ''
                 OR COALESCE(focus_next, '') != ''
                 OR COALESCE(spotted_problems, '') != ''
                 OR COALESCE(outside_control, '') != ''
                 OR COALESCE(within_control, '') != ''
                 OR COALESCE(attribution, '') != ''
                 OR COALESCE(personal_contribution, '') != ''
                 OR EXISTS (
                        SELECT 1
                        FROM session_log
                        WHERE session_log.game_id = games.game_id
                          AND (
                                COALESCE(session_log.improvement_note, '') != ''
                             OR COALESCE(session_log.mental_handled, '') != ''
                             OR COALESCE(session_log.is_skipped, 0) = 1
                          )
                    )
                 OR EXISTS (
                        SELECT 1
                        FROM game_concept_tags
                        WHERE game_concept_tags.game_id = games.game_id
                    )
                 OR {ReviewedByCoachBlockTerm}
              )
              {CasualFilter}
              AND {RemakeSql.NotRemake("games.game_id")}
            ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await ReadAllGamesAsync(cmd);
    }

    // ── Aggregation reads ────────────────────────────────────────────────

}
