#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// CRUD operations for the session_log and sessions tables —
/// ported from Python SessionLogRepository.
/// </summary>
public sealed class SessionLogRepository : ISessionLogRepository
{
    private readonly IDbConnectionFactory _factory;

    public SessionLogRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SessionLogEntry MapSessionLogEntry(SqliteDataReader reader)
    {
        return new SessionLogEntry
        {
            Id = GetIntOrDefault(reader, "id"),
            Date = GetStringOrDefault(reader, "date"),
            GameId = reader.IsDBNull(reader.GetOrdinal("game_id")) ? null : reader.GetInt64(reader.GetOrdinal("game_id")),
            ChampionName = GetStringOrDefault(reader, "champion_name"),
            Win = !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0,
            MentalRating = GetIntOrDefault(reader, "mental_rating"),
            ImprovementNote = GetStringOrDefault(reader, "improvement_note"),
            RuleBroken = GetIntOrDefault(reader, "rule_broken"),
            Timestamp = reader.IsDBNull(reader.GetOrdinal("timestamp")) ? 0 : reader.GetInt64(reader.GetOrdinal("timestamp")),
            MentalHandled = GetStringOrDefault(reader, "mental_handled"),
            PreGameMood = GetIntOrDefault(reader, "pre_game_mood"),
        };
    }

    private static SessionInfo MapSessionInfo(SqliteDataReader reader)
    {
        return new SessionInfo
        {
            Id = GetIntOrDefault(reader, "id"),
            Date = GetStringOrDefault(reader, "date"),
            Intention = GetStringOrDefault(reader, "intention"),
            DebriefRating = GetIntOrDefault(reader, "debrief_rating"),
            DebriefNote = GetStringOrDefault(reader, "debrief_note"),
            StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at")) ? null : reader.GetInt64(reader.GetOrdinal("started_at")),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("ended_at")) ? null : reader.GetInt64(reader.GetOrdinal("ended_at")),
        };
    }

    private static int GetIntOrDefault(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? 0 : r.GetInt32(ord);
    }

    private static double GetDoubleOrDefault(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? 0.0 : r.GetDouble(ord);
    }

    private static string GetStringOrDefault(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? "" : r.GetString(ord);
    }

    private static async Task<List<SessionLogEntry>> ReadAllEntriesAsync(SqliteCommand cmd)
    {
        var list = new List<SessionLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapSessionLogEntry(reader));
        }
        return list;
    }

    // ── Write operations ─────────────────────────────────────────────────

    public async Task LogGameAsync(
        long gameId,
        string championName,
        bool win,
        int mentalRating = 5,
        string improvementNote = "",
        int preGameMood = 0,
        bool ruleBroken = false)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        var (sessionDate, sessionTimestamp) = await ResolveGameSessionDateAsync(conn, gameId);

        // v2.16: a prior user clear is sticky — never re-flag a cleared game.
        if (ruleBroken && await IsRuleBreakClearedAsync(conn, gameId))
        {
            ruleBroken = false;
        }

        // Check if entry already exists
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT id FROM session_log WHERE game_id = @gameId";
            checkCmd.Parameters.AddWithValue("@gameId", gameId);
            var existing = await checkCmd.ExecuteScalarAsync();

            if (existing is not null)
            {
                // Update existing entry
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE session_log
                    SET date = @date,
                        champion_name = @champion_name,
                        win = @win,
                        mental_rating = @mental,
                        improvement_note = @note,
                        timestamp = @timestamp,
                        pre_game_mood = @pre_game_mood
                    WHERE game_id = @gameId";
                updateCmd.Parameters.AddWithValue("@date", sessionDate);
                updateCmd.Parameters.AddWithValue("@champion_name", championName);
                updateCmd.Parameters.AddWithValue("@win", win ? 1 : 0);
                updateCmd.Parameters.AddWithValue("@mental", mentalRating);
                updateCmd.Parameters.AddWithValue("@note", improvementNote);
                updateCmd.Parameters.AddWithValue("@timestamp", sessionTimestamp);
                updateCmd.Parameters.AddWithValue("@pre_game_mood", preGameMood);
                updateCmd.Parameters.AddWithValue("@gameId", gameId);
                await updateCmd.ExecuteNonQueryAsync();
                return;
            }
        }

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO session_log
            (date, game_id, champion_name, win, mental_rating, improvement_note,
            rule_broken, timestamp, pre_game_mood)
            VALUES (@date, @game_id, @champion_name, @win, @mental_rating, @improvement_note,
                    @rule_broken, @timestamp, @pre_game_mood)";

        insertCmd.Parameters.AddWithValue("@date", sessionDate);
        insertCmd.Parameters.AddWithValue("@game_id", gameId);
        insertCmd.Parameters.AddWithValue("@champion_name", championName);
        insertCmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        insertCmd.Parameters.AddWithValue("@mental_rating", mentalRating);
        insertCmd.Parameters.AddWithValue("@improvement_note", improvementNote);
        insertCmd.Parameters.AddWithValue("@rule_broken", ruleBroken ? 1 : 0);
        insertCmd.Parameters.AddWithValue("@timestamp", sessionTimestamp);
        insertCmd.Parameters.AddWithValue("@pre_game_mood", preGameMood);

        await insertCmd.ExecuteNonQueryAsync();
    }

    private static async Task<(string SessionDate, long SessionTimestamp)> ResolveGameSessionDateAsync(
        SqliteConnection conn,
        long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT date_played, timestamp
            FROM games
            WHERE game_id = @gameId
            LIMIT 1";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var datePlayed = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var timestamp = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);

            if (!string.IsNullOrWhiteSpace(datePlayed) && datePlayed.Length >= 10)
            {
                return (datePlayed[..10], timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }

            if (timestamp > 0)
            {
                var localDate = DateTimeOffset
                    .FromUnixTimeSeconds(timestamp)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd");
                return (localDate, timestamp);
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (DateTime.Now.ToString("yyyy-MM-dd"), now);
    }

    public async Task UpdateMentalRatingAsync(long gameId, int mentalRating)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session_log SET mental_rating = @mental WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@mental", mentalRating);
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkSkippedAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session_log SET is_skipped = 1 WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearSkippedAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session_log SET is_skipped = 0 WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateMentalHandledAsync(long gameId, string mentalHandled)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session_log SET mental_handled = @handled WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@handled", mentalHandled);
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetRuleBrokenAsync(long gameId, bool ruleBroken)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE session_log SET rule_broken = @ruleBroken WHERE game_id = @gameId";
            cmd.Parameters.AddWithValue("@ruleBroken", ruleBroken ? 1 : 0);
            cmd.Parameters.AddWithValue("@gameId", gameId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Sticky cleared-stamp: clearing inserts, re-flagging removes. The stamp
        // outlives the session_log row so subsequent rule re-evaluation can't
        // overturn the user's decision.
        using (var stickyCmd = conn.CreateCommand())
        {
            if (ruleBroken)
            {
                stickyCmd.CommandText = "DELETE FROM cleared_rule_breaks WHERE game_id = @gameId";
                stickyCmd.Parameters.AddWithValue("@gameId", gameId);
            }
            else
            {
                stickyCmd.CommandText = @"
                    INSERT INTO cleared_rule_breaks (game_id, cleared_at)
                    VALUES (@gameId, @clearedAt)
                    ON CONFLICT(game_id) DO UPDATE SET cleared_at = excluded.cleared_at";
                stickyCmd.Parameters.AddWithValue("@gameId", gameId);
                stickyCmd.Parameters.AddWithValue("@clearedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            await stickyCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> IsRuleBreakClearedAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();
        return await IsRuleBreakClearedAsync(conn, gameId);
    }

    private static async Task<bool> IsRuleBreakClearedAsync(SqliteConnection conn, long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM cleared_rule_breaks WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }

    public async Task SetSessionIntentionAsync(string dateStr, string intention)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sessions (date, intention, started_at)
            VALUES (@date, @intention, @started_at)
            ON CONFLICT(date) DO UPDATE SET intention = excluded.intention";
        cmd.Parameters.AddWithValue("@date", dateStr);
        cmd.Parameters.AddWithValue("@intention", intention);
        cmd.Parameters.AddWithValue("@started_at", now);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveSessionDebriefAsync(string dateStr, int rating, string note = "")
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sessions (date, debrief_rating, debrief_note, ended_at)
            VALUES (@date, @rating, @note, @ended_at)
            ON CONFLICT(date) DO UPDATE SET
                debrief_rating = excluded.debrief_rating,
                debrief_note = excluded.debrief_note,
                ended_at = excluded.ended_at";
        cmd.Parameters.AddWithValue("@date", dateStr);
        cmd.Parameters.AddWithValue("@rating", rating);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@ended_at", now);

        await cmd.ExecuteNonQueryAsync();
    }

    // ── Single reads ─────────────────────────────────────────────────────

    public async Task<SessionLogEntry?> GetEntryAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM session_log WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSessionLogEntry(reader) : null;
    }

    public async Task<bool> HasEntryAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM session_log WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }

    public async Task<SessionInfo?> GetSessionAsync(string dateStr)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE date = @date";
        cmd.Parameters.AddWithValue("@date", dateStr);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSessionInfo(reader) : null;
    }

    // ── List reads ───────────────────────────────────────────────────────

    public async Task<List<SessionLogEntry>> GetForDateAsync(string dateStr)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM session_log
            WHERE date = @date
            ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@date", dateStr);

        return await ReadAllEntriesAsync(cmd);
    }

    public async Task<List<SessionLogEntry>> GetTodayAsync()
    {
        return await GetForDateAsync(DateTime.Now.ToString("yyyy-MM-dd"));
    }

    public async Task<List<string>> GetDatesWithGamesAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT date FROM session_log ORDER BY date DESC";

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public async Task<List<SessionLogEntry>> GetRangeAsync(int days = 7)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        var cutoff = DateTime.Today.AddDays(-(days - 1));
        var cutoffStr = cutoff.ToString("yyyy-MM-dd");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM session_log
            WHERE date >= @cutoff
            ORDER BY date DESC, timestamp ASC";
        cmd.Parameters.AddWithValue("@cutoff", cutoffStr);

        return await ReadAllEntriesAsync(cmd);
    }

    // ── Cleanup ──────────────────────────────────────────────────────────

    public async Task<int> CleanupMismatchedEntriesAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM session_log
            WHERE game_id IN (
                SELECT sl.game_id FROM session_log sl
                JOIN games g ON sl.game_id = g.game_id
                WHERE CASE
                    WHEN COALESCE(g.date_played, '') != '' THEN sl.date != SUBSTR(g.date_played, 1, 10)
                    WHEN COALESCE(g.timestamp, 0) > 0 THEN sl.date != DATE(g.timestamp, 'unixepoch', 'localtime')
                    ELSE 0
                END
            )";

        var deleted = await cmd.ExecuteNonQueryAsync();
        return deleted;
    }

    // ── Aggregation reads ────────────────────────────────────────────────

    public async Task<SessionDayStats> GetStatsTodayAsync()
    {
        return await GetStatsForDateAsync(DateTime.Now.ToString("yyyy-MM-dd"));
    }

    public async Task<SessionDayStats> GetStatsForDateAsync(string dateStr)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as games,
                SUM(win) as wins,
                COUNT(*) - SUM(win) as losses,
                ROUND(AVG(CASE WHEN COALESCE(is_skipped, 0) = 0 THEN mental_rating END), 1) as avg_mental,
                SUM(rule_broken) as rule_breaks
            FROM session_log
            WHERE date = @date";
        cmd.Parameters.AddWithValue("@date", dateStr);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var games = reader.IsDBNull(reader.GetOrdinal("games")) ? 0 : reader.GetInt32(reader.GetOrdinal("games"));
            if (games > 0)
            {
                return new SessionDayStats(
                    Games: games,
                    Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                    Losses: reader.IsDBNull(reader.GetOrdinal("losses")) ? 0 : reader.GetInt32(reader.GetOrdinal("losses")),
                    AvgMental: reader.IsDBNull(reader.GetOrdinal("avg_mental")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_mental")),
                    RuleBreaks: reader.IsDBNull(reader.GetOrdinal("rule_breaks")) ? 0 : reader.GetInt32(reader.GetOrdinal("rule_breaks"))
                );
            }
        }

        return new SessionDayStats(0, 0, 0, 0, 0);
    }

    public async Task<List<DailySummary>> GetDailySummariesAsync(int days = 7)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        var cutoff = DateTime.Today.AddDays(-(days - 1));
        var cutoffStr = cutoff.ToString("yyyy-MM-dd");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                date,
                COUNT(*) as games,
                SUM(win) as wins,
                COUNT(*) - SUM(win) as losses,
                ROUND(AVG(CASE WHEN COALESCE(is_skipped, 0) = 0 THEN mental_rating END), 1) as avg_mental,
                SUM(rule_broken) as rule_breaks,
                GROUP_CONCAT(DISTINCT champion_name) as champions_played
            FROM session_log
            WHERE date >= @cutoff
            GROUP BY date
            ORDER BY date DESC";
        cmd.Parameters.AddWithValue("@cutoff", cutoffStr);

        var list = new List<DailySummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new DailySummary(
                Date: reader.GetString(reader.GetOrdinal("date")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Losses: reader.IsDBNull(reader.GetOrdinal("losses")) ? 0 : reader.GetInt32(reader.GetOrdinal("losses")),
                AvgMental: reader.IsDBNull(reader.GetOrdinal("avg_mental")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_mental")),
                RuleBreaks: reader.IsDBNull(reader.GetOrdinal("rule_breaks")) ? 0 : reader.GetInt32(reader.GetOrdinal("rule_breaks")),
                ChampionsPlayed: reader.IsDBNull(reader.GetOrdinal("champions_played")) ? "" : reader.GetString(reader.GetOrdinal("champions_played"))
            ));
        }
        return list;
    }

    public async Task<int> GetAdherenceStreakAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        // Only count adherence from the date the first user-defined rule was created.
        // Before that point rule_broken flags were stamped by a since-removed heuristic
        // and don't reflect real rule violations.
        string? rulesExistSince = null;
        using (var rulesCmd = conn.CreateCommand())
        {
            rulesCmd.CommandText = "SELECT MIN(DATE(created_at, 'unixepoch', 'localtime')) FROM rules";
            var val = await rulesCmd.ExecuteScalarAsync();
            if (val is string s && !string.IsNullOrEmpty(s))
                rulesExistSince = s;
        }

        using var cmd = conn.CreateCommand();
        if (rulesExistSince is not null)
        {
            cmd.CommandText = @"
                SELECT date, SUM(rule_broken) as breaks
                FROM session_log
                WHERE date >= @since
                GROUP BY date
                ORDER BY date DESC";
            cmd.Parameters.AddWithValue("@since", rulesExistSince);
        }
        else
        {
            // No rules ever created — adherence streak is always 0
            return 0;
        }

        var streak = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var breaks = reader.IsDBNull(reader.GetOrdinal("breaks")) ? 0 : reader.GetInt32(reader.GetOrdinal("breaks"));
            if (breaks == 0)
                streak++;
            else
                break;
        }
        return streak;
    }

    public async Task<List<MentalCorrelationPoint>> GetMentalWinrateCorrelationAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                CASE
                    WHEN mental_rating <= 3 THEN '1-3 (Low)'
                    WHEN mental_rating <= 6 THEN '4-6 (Mid)'
                    ELSE '7-10 (High)'
                END as bracket,
                COUNT(*) as games,
                SUM(win) as wins,
                ROUND(AVG(win) * 100, 1) as winrate
            FROM session_log
            WHERE COALESCE(is_skipped, 0) = 0
            GROUP BY bracket
            ORDER BY bracket";

        var list = new List<MentalCorrelationPoint>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MentalCorrelationPoint(
                Bracket: reader.GetString(reader.GetOrdinal("bracket")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate"))
            ));
        }
        return list;
    }

    public async Task<List<MentalTrendPoint>> GetMentalTrendAsync(int limit = 50)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT timestamp, mental_rating, win, champion_name
            FROM session_log
            WHERE COALESCE(is_skipped, 0) = 0
            ORDER BY timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MentalTrendPoint>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MentalTrendPoint(
                Timestamp: reader.IsDBNull(reader.GetOrdinal("timestamp")) ? 0 : reader.GetInt64(reader.GetOrdinal("timestamp")),
                MentalRating: reader.IsDBNull(reader.GetOrdinal("mental_rating")) ? 0 : reader.GetInt32(reader.GetOrdinal("mental_rating")),
                Win: !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0,
                ChampionName: reader.IsDBNull(reader.GetOrdinal("champion_name")) ? "" : reader.GetString(reader.GetOrdinal("champion_name"))
            ));
        }

        // Python returns reversed(rows) — chronological order
        list.Reverse();
        return list;
    }

    public async Task<List<MoodCorrelationPoint>> GetMoodWinrateCorrelationAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pre_game_mood as mood,
                COUNT(*) as games,
                SUM(win) as wins,
                ROUND(AVG(win) * 100, 1) as winrate
            FROM session_log
            WHERE pre_game_mood > 0
            GROUP BY pre_game_mood
            ORDER BY pre_game_mood";

        var list = new List<MoodCorrelationPoint>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MoodCorrelationPoint(
                Mood: reader.GetInt32(reader.GetOrdinal("mood")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate"))
            ));
        }
        return list;
    }

    public async Task<TiltWarning?> CheckTiltWarningAsync(string dateStr)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT mental_rating, champion_name, game_id
            FROM session_log
            WHERE date = @date
              AND COALESCE(is_skipped, 0) = 0
            ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@date", dateStr);

        var entries = new List<(int MentalRating, string ChampionName, long? GameId)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add((
                MentalRating: reader.IsDBNull(reader.GetOrdinal("mental_rating")) ? 0 : reader.GetInt32(reader.GetOrdinal("mental_rating")),
                ChampionName: reader.IsDBNull(reader.GetOrdinal("champion_name")) ? "" : reader.GetString(reader.GetOrdinal("champion_name")),
                GameId: reader.IsDBNull(reader.GetOrdinal("game_id")) ? null : (long?)reader.GetInt64(reader.GetOrdinal("game_id"))
            ));
        }

        if (entries.Count < 2)
            return null;

        for (int i = 1; i < entries.Count; i++)
        {
            var prev = entries[i - 1].MentalRating;
            var curr = entries[i].MentalRating;
            if (prev - curr >= 3)
            {
                return new TiltWarning(
                    FromMental: prev,
                    ToMental: curr,
                    GameChampion: entries[i].ChampionName,
                    GameId: entries[i].GameId
                );
            }
        }

        return null;
    }

    public async Task<SessionPatterns> GetSessionPatternsAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        // Average games per session day
        double avgGames;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT ROUND(AVG(day_count), 1) as avg_games_per_session
                FROM (SELECT date, COUNT(*) as day_count
                      FROM session_log GROUP BY date)";

            await using var reader = await cmd.ExecuteReaderAsync();
            avgGames = await reader.ReadAsync() && !reader.IsDBNull(0) ? reader.GetDouble(0) : 0;
        }

        // Mental delta: avg difference between first and last game mental per day
        double avgMentalDelta;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT date,
                        FIRST_VALUE(mental_rating) OVER (PARTITION BY date ORDER BY timestamp) as first_mental,
                        FIRST_VALUE(mental_rating) OVER (PARTITION BY date ORDER BY timestamp DESC) as last_mental
                FROM session_log
                WHERE mental_rating IS NOT NULL
                  AND COALESCE(is_skipped, 0) = 0
                GROUP BY date";

            var deltas = new List<double>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(reader.GetOrdinal("first_mental")) &&
                    !reader.IsDBNull(reader.GetOrdinal("last_mental")))
                {
                    var first = reader.GetInt32(reader.GetOrdinal("first_mental"));
                    var last = reader.GetInt32(reader.GetOrdinal("last_mental"));
                    if (first != 0 && last != 0)
                        deltas.Add(last - first);
                }
            }

            avgMentalDelta = deltas.Count > 0 ? Math.Round(deltas.Average(), 1) : 0;
        }

        // Tilt frequency: % of days with a rule broken
        int totalDays;
        int tiltDays;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT
                        COUNT(DISTINCT date) as total_days,
                        COUNT(DISTINCT CASE WHEN rule_broken = 1 THEN date END) as tilt_days
                FROM session_log";

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                totalDays = reader.IsDBNull(reader.GetOrdinal("total_days")) ? 0 : reader.GetInt32(reader.GetOrdinal("total_days"));
                tiltDays = reader.IsDBNull(reader.GetOrdinal("tilt_days")) ? 0 : reader.GetInt32(reader.GetOrdinal("tilt_days"));
            }
            else
            {
                totalDays = 0;
                tiltDays = 0;
            }
        }

        var tiltPct = totalDays > 0 ? Math.Round(100.0 * tiltDays / totalDays, 1) : 0;

        return new SessionPatterns(
            AvgGamesPerSession: avgGames,
            AvgMentalDelta: avgMentalDelta,
            TiltFrequencyPct: tiltPct,
            TotalSessionDays: totalDays
        );
    }
}
