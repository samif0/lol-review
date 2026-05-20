#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    public async Task SetHiddenAsync(long gameId, bool hidden)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET is_hidden = @hidden WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@hidden", hidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async Task<string> DeleteAsync(long gameId)
    {
        // Always snapshot the DB first â€” deletion cascades across 13 child
        // tables and there is no per-row undo path. The safety backup gives
        // the user an out of last resort.
        await _backupService.CreateSafetyBackupAsync($"delete-game-{gameId}")
            .ConfigureAwait(false);

        // Child tables that reference games.game_id. Listed explicitly so that
        // adding a new child-with-game_id in the schema forces this list to
        // be updated â€” silent orphan rows are the failure mode we're avoiding.
        //
        // Deliberately absent: missed_game_decisions. We want the row in that
        // table to survive (as 'dismissed') so the reconciler doesn't re-offer
        // this game on the next startup after Riot's match history still has it.
        // The tombstone is written explicitly below in the same transaction.
        string[] childTables =
        {
            "evidence_items",
            "session_log",
            "vod_files",
            "game_events",
            "derived_event_instances",
            "game_objectives",
            "game_concept_tags",
            "prompt_answers",
            "matchup_notes",
            "tilt_checks",
            "review_drafts",
            "cleared_rule_breaks",
            "game_summary",
            "review_concepts",
            "feature_values",
        };

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Collect clip file paths BEFORE the row is gone â€” we need them to
        // clean up on-disk clip extractions after the transaction commits.
        // VOD source recordings are owned by Ascent; we only touch clips.
        var clipPaths = new List<string>();
        try
        {
            using (var clipsCmd = conn.CreateCommand())
            {
                clipsCmd.Transaction = tx;
                clipsCmd.CommandText =
                    "SELECT clip_path FROM vod_bookmarks WHERE game_id = @gameId AND clip_path != ''";
                clipsCmd.Parameters.AddWithValue("@gameId", gameId);
                using var reader = await clipsCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var p = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(p)) clipPaths.Add(p);
                }
            }

            var gamePk = await GetGamePkAsync(conn, tx, gameId).ConfigureAwait(false);

            await DeleteByCoachMomentGameAsync(conn, tx, "coach_labels", gameId, gamePk).ConfigureAwait(false);
            await DeleteByCoachMomentGameAsync(conn, tx, "coach_inferences", gameId, gamePk).ConfigureAwait(false);
            await DeleteByGameIdAsync(conn, tx, "coach_moments", gameId, gamePk).ConfigureAwait(false);
            await DeleteByGameIdAsync(conn, tx, "vod_bookmarks", gameId, gamePk).ConfigureAwait(false);

            foreach (var table in childTables)
            {
                await DeleteByGameIdAsync(conn, tx, table, gameId, gamePk).ConfigureAwait(false);
            }

            // Tombstone: mark the game_id as permanently dismissed in
            // missed_game_decisions. Without this, the next startup's
            // reconciler would see Riot still listing this match and
            // re-offer it for ingestion because the games-table row is
            // gone. Upsert so we cleanly handle "user previously dismissed
            // then later deleted" or vice-versa.
            using (var tombstoneCmd = conn.CreateCommand())
            {
                tombstoneCmd.Transaction = tx;
                tombstoneCmd.CommandText = """
                    INSERT INTO missed_game_decisions (game_id, decision, created_at, updated_at)
                    VALUES (@gameId, 'dismissed', @now, @now)
                    ON CONFLICT(game_id) DO UPDATE SET
                        decision = 'dismissed',
                        updated_at = excluded.updated_at
                    """;
                tombstoneCmd.Parameters.AddWithValue("@gameId", gameId);
                tombstoneCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await tombstoneCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var gamesCmd = conn.CreateCommand())
            {
                gamesCmd.Transaction = tx;
                gamesCmd.CommandText = "DELETE FROM games WHERE game_id = @gameId";
                gamesCmd.Parameters.AddWithValue("@gameId", gameId);
                await gamesCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        // Delete clip files after the transaction commits. File-system
        // failures here are non-fatal â€” the DB is already consistent; a
        // leftover clip just lingers on disk until the user clears it or the
        // folder-size limiter prunes it.
        foreach (var clipPath in clipPaths)
        {
            try
            {
                if (File.Exists(clipPath)) File.Delete(clipPath);
            }
            catch
            {
                // Swallow â€” best-effort cleanup.
            }
        }

        var dbDir = Path.GetDirectoryName(_factory.DatabasePath) ?? "";
        return Path.Combine(dbDir, "backups");
    }

    private static async Task<long?> GetGamePkAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        long gameId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM games WHERE game_id = @gameId LIMIT 1";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private static async Task DeleteByGameIdAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string table,
        long gameId,
        long? gamePk)
    {
        if (!await TableExistsAsync(conn, tx, table).ConfigureAwait(false))
        {
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = gamePk.HasValue
            ? $"DELETE FROM {table} WHERE game_id = @gameId OR game_id = @gamePk"
            : $"DELETE FROM {table} WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        if (gamePk.HasValue)
        {
            cmd.Parameters.AddWithValue("@gamePk", gamePk.Value);
        }

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task DeleteByCoachMomentGameAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string table,
        long gameId,
        long? gamePk)
    {
        if (!await TableExistsAsync(conn, tx, table).ConfigureAwait(false)
            || !await TableExistsAsync(conn, tx, "coach_moments").ConfigureAwait(false))
        {
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = gamePk.HasValue
            ? $"""
               DELETE FROM {table}
               WHERE moment_id IN (
                   SELECT id FROM coach_moments WHERE game_id = @gameId OR game_id = @gamePk
               )
               """
            : $"""
               DELETE FROM {table}
               WHERE moment_id IN (
                   SELECT id FROM coach_moments WHERE game_id = @gameId
               )
               """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        if (gamePk.HasValue)
        {
            cmd.Parameters.AddWithValue("@gamePk", gamePk.Value);
        }

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @table LIMIT 1";
        cmd.Parameters.AddWithValue("@table", table);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is not null && result != DBNull.Value;
    }

    // â”€â”€ Single reads â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

}
