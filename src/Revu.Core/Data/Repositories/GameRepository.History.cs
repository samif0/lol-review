#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    public async Task<IReadOnlyList<long>> GetGameIdsMissingEnemyLanerAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // v2.16: also pick up games that have enemy_laner but a blank
        // participant_map. Pre-v2.16 backfill only stored the lane opponent;
        // now we want the full 10-champion map for role-aware matchup pills,
        // so any row missing EITHER column needs a Match-V5 round-trip.
        cmd.CommandText = $@"
            SELECT game_id FROM games
            WHERE (
                    (enemy_laner IS NULL OR enemy_laner = '')
                 OR (participant_map IS NULL OR participant_map = '')
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
        cmd.CommandText = "SELECT * FROM games WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapGameStats(reader) : null;
    }

    // â”€â”€ List reads â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
            $"SELECT * FROM games WHERE {string.Join(" AND ", whereClauses)} {CasualFilter} " +
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
            )
            {CasualFilter}
            AND (is_hidden IS NULL OR is_hidden = 0)";

        var result = await cmd.ExecuteScalarAsync();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    public async Task<List<GameStats>> GetGamesForDateAsync(string dateStr)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM games WHERE date_played LIKE @datePat {CasualFilter} ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@datePat", $"{dateStr}%");

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<GameStats>> GetTodaysGamesAsync()
    {
        using var conn = _factory.CreateConnection();

        var todayStart = DateTime.Today;
        var todayTimestamp = new DateTimeOffset(todayStart).ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM games WHERE timestamp >= @ts {CasualFilter} ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@ts", todayTimestamp);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<GameStats>> GetLossesAsync(string? champion = null)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(champion) && champion != "All Champions")
        {
            cmd.CommandText = $"SELECT * FROM games WHERE win = 0 AND champion_name = @champ {CasualFilter} ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue("@champ", champion);
        }
        else
        {
            cmd.CommandText = $"SELECT * FROM games WHERE win = 0 {CasualFilter} ORDER BY timestamp DESC";
        }

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
              )
              {CasualFilter}
            ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<string>> GetUniqueChampionsAsync(bool lossesOnly = false)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        if (lossesOnly)
        {
            cmd.CommandText = $"SELECT DISTINCT champion_name FROM games WHERE win = 0 {CasualFilter} ORDER BY champion_name";
        }
        else
        {
            cmd.CommandText = $"SELECT DISTINCT champion_name FROM games WHERE 1=1 {CasualFilter} ORDER BY champion_name";
        }

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    // â”€â”€ Aggregation reads â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

}
