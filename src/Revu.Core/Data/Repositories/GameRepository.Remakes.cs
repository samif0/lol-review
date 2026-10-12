#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// 3.14.1: remake verdicts (game_remakes). A remake is saved and reviewable but never
/// counted; the exclusion fragment lives in <see cref="RemakeSql"/>.
/// </summary>
public sealed partial class GameRepository
{
    private static readonly string RemakeUpsertSql = $"""
        INSERT INTO game_remakes (game_id, is_remake, caused_by_self, source, checked_at)
        VALUES (@id, @r, @c, @s, @t)
        ON CONFLICT(game_id) DO UPDATE SET
            is_remake = excluded.is_remake,
            caused_by_self = excluded.caused_by_self,
            source = excluded.source,
            checked_at = excluded.checked_at
        WHERE ({string.Format(RemakeSql.RankSql, "excluded.source")}) >= ({string.Format(RemakeSql.RankSql, "game_remakes.source")})
        """;

    /// <inheritdoc />
    public async Task<RemakeWriteResult> SetRemakeVerdictAsync(long gameId, bool isRemake, bool causedBySelf, string source,
        DbWriteSession? session = null)
    {
        if (gameId <= 0) throw new ArgumentOutOfRangeException(nameof(gameId), "A remake verdict needs a game id.");
        if (!RemakeSql.IsKnownSource(source)) throw new ArgumentException($"Unknown remake source '{source}'.", nameof(source));
        if (isRemake && source == RemakeSql.SourceUnknown)
            throw new ArgumentException("An unanswered lookup cannot mark a remake.", nameof(isRemake));

        using var lease = DbWriteSession.Lease(_factory, session, transactional: true);
        var conn = lease.Connection;
        var tx = lease.Transaction!;
        RemakeWriteResult result;
        try
        {
            result = await WriteRemakeVerdictAsync(conn, tx, gameId, isRemake, causedBySelf, source).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (RemakeSql.IsMissingTable(ex))
        {
            // A database a downgraded build last opened: create the table, retry once.
            await RemakeSql.EnsureTableAsync(conn, tx).ConfigureAwait(false);
            result = await WriteRemakeVerdictAsync(conn, tx, gameId, isRemake, causedBySelf, source).ConfigureAwait(false);
        }
        lease.Complete();
        return result;
    }

    private static async Task<RemakeWriteResult> WriteRemakeVerdictAsync(
        SqliteConnection conn, SqliteTransaction tx, long gameId, bool isRemake, bool causedBySelf, string source)
    {
        var before = await ReadIsRemakeAsync(conn, tx, gameId).ConfigureAwait(false);

        int applied;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = RemakeUpsertSql;
            cmd.Parameters.AddWithValue("@id", gameId);
            cmd.Parameters.AddWithValue("@r", isRemake ? 1 : 0);
            cmd.Parameters.AddWithValue("@c", causedBySelf ? 1 : 0);
            cmd.Parameters.AddWithValue("@s", source);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            applied = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var after = await ReadIsRemakeAsync(conn, tx, gameId).ConfigureAwait(false);
        var changed = (before ?? false) != (after ?? false);
        if (changed) await RebaseObjectiveGameCountsAsync(conn, tx, gameId).ConfigureAwait(false);
        return new RemakeWriteResult(Applied: applied > 0, Changed: changed);
    }

    // A verdict that flips after the review was saved re-bases game_count for this game's
    // objectives (same count as the startup recompute), so a remake stops counting at once.
    private static async Task RebaseObjectiveGameCountsAsync(SqliteConnection conn, SqliteTransaction tx, long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE objectives SET game_count = COALESCE((
                SELECT COUNT(*) FROM game_objectives
                JOIN games g ON g.game_id = game_objectives.game_id
                WHERE game_objectives.objective_id = objectives.id
                  AND COALESCE(g.is_hidden, 0) = 0
                  AND {RemakeSql.NotRemake("g.game_id")}
            ), 0)
            WHERE id IN (SELECT objective_id FROM game_objectives WHERE game_id = @id)
            """;
        cmd.Parameters.AddWithValue("@id", gameId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool?> ReadIsRemakeAsync(SqliteConnection conn, SqliteTransaction tx, long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT is_remake FROM game_remakes WHERE game_id = @id";
        cmd.Parameters.AddWithValue("@id", gameId);
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null || value == DBNull.Value ? null : Convert.ToInt64(value) != 0;
    }

    /// <inheritdoc />
    public async Task<RemakeVerdict?> GetRemakeVerdictAsync(long gameId)
    {
        try
        {
            using var conn = _factory.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT is_remake, caused_by_self, source, checked_at
                FROM game_remakes WHERE game_id = @id
                """;
            cmd.Parameters.AddWithValue("@id", gameId);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return null;
            return new RemakeVerdict(
                IsRemake: !reader.IsDBNull(0) && reader.GetInt64(0) != 0,
                CausedBySelf: !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                Source: reader.IsDBNull(2) ? "" : reader.GetString(2),
                CheckedAt: reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
        }
        catch (SqliteException ex) when (RemakeSql.IsMissingTable(ex))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<RemakeCandidate>> GetRemakeCandidatesAsync(int maxDurationS, int limit)
    {
        var list = new List<RemakeCandidate>();
        try
        {
            using var conn = _factory.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT game_id, COALESCE(game_duration, 0), COALESCE(timestamp, 0) FROM games
                WHERE COALESCE(is_hidden, 0) = 0
                  AND COALESCE(queue_type, '') = 'Ranked Solo/Duo'
                  AND COALESCE(game_duration, 0) > 0
                  AND COALESCE(game_duration, 0) < @maxDuration
                  AND NOT EXISTS (SELECT 1 FROM game_remakes r WHERE r.game_id = games.game_id)
                ORDER BY timestamp DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@maxDuration", maxDurationS);
            cmd.Parameters.AddWithValue("@limit", Math.Max(0, limit));
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;
                list.Add(new RemakeCandidate(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2)));
            }
        }
        catch (SqliteException ex) when (RemakeSql.IsMissingTable(ex))
        {
            // No table yet means nothing was ever checked; startup creates it before the sweep.
        }
        return list;
    }

    /// <inheritdoc />
    public async Task<List<RuleCheckGame>> GetTodaysRuleGamesAsync()
    {
        using var conn = _factory.CreateConnection();
        var todayTimestamp = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT game_id, win, champion_name, timestamp FROM games
            WHERE timestamp >= @ts {CasualFilter}
              AND {RemakeSql.NotRemake("games.game_id")}
            ORDER BY timestamp ASC
            """;
        cmd.Parameters.AddWithValue("@ts", todayTimestamp);

        var games = new List<RuleCheckGame>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            games.Add(new RuleCheckGame(
                GameId: reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                Win: !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                ChampionName: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Timestamp: reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
        }
        return games;
    }
}
