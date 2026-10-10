#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Shared clip_narrations SQL used by <see cref="ClipNarrationRepository"/> and by the
/// bookmark/clip/game delete paths (foreign keys are off, so every delete path removes
/// narration rows explicitly, inside its own transaction). Every helper tolerates a
/// database without the table.
/// </summary>
internal static class ClipNarrationSql
{
    public const string Table = "clip_narrations";

    public const string Columns = """
        bookmark_id, game_id, narration_id, audio_path, narrated_clip_path, source_clip_path,
        offset_ms, duration_ms, game_volume, narration_volume, duck, transcript_status,
        transcript_generation, transcript_language, transcript_json, transcript_error,
        transcript_pushed_slug, created_at, updated_at
        """;

    public static bool IsMissingTable(SqliteException ex) =>
        ex.SqliteErrorCode == 1
        && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
        && ex.Message.Contains(Table, StringComparison.OrdinalIgnoreCase);

    /// <summary>Create the table + index (same DDL AllCreateStatements runs at startup).</summary>
    public static async Task EnsureTableAsync(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        foreach (var ddl in new[] { Schema.CreateClipNarrationsTable, Schema.CreateClipNarrationsGameIndex })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public static ClipNarrationRecord Read(SqliteDataReader r) => new(
        BookmarkId: r.GetInt64(0),
        GameId: r.IsDBNull(1) ? 0 : r.GetInt64(1),
        NarrationId: r.IsDBNull(2) ? "" : r.GetString(2),
        AudioPath: r.IsDBNull(3) ? "" : r.GetString(3),
        NarratedClipPath: r.IsDBNull(4) ? "" : r.GetString(4),
        SourceClipPath: r.IsDBNull(5) ? "" : r.GetString(5),
        OffsetMs: r.IsDBNull(6) ? 0 : r.GetInt32(6),
        DurationMs: r.IsDBNull(7) ? 0 : r.GetInt32(7),
        GameVolume: r.IsDBNull(8) ? 0.8 : r.GetDouble(8),
        NarrationVolume: r.IsDBNull(9) ? 1.0 : r.GetDouble(9),
        Duck: !r.IsDBNull(10) && r.GetInt64(10) != 0,
        TranscriptStatus: r.IsDBNull(11) ? "" : r.GetString(11),
        TranscriptGeneration: r.IsDBNull(12) ? 0 : r.GetInt64(12),
        TranscriptLanguage: r.IsDBNull(13) ? "" : r.GetString(13),
        TranscriptJson: r.IsDBNull(14) ? "" : r.GetString(14),
        TranscriptError: r.IsDBNull(15) ? "" : r.GetString(15),
        TranscriptPushedSlug: r.IsDBNull(16) ? "" : r.GetString(16),
        CreatedAt: r.IsDBNull(17) ? 0 : r.GetInt64(17),
        UpdatedAt: r.IsDBNull(18) ? 0 : r.GetInt64(18));

    /// <summary>The row for one bookmark, or null (also null when the table is missing).</summary>
    public static async Task<ClipNarrationRecord?> ReadAsync(SqliteConnection conn, SqliteTransaction? tx, long bookmarkId)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT {Columns} FROM clip_narrations WHERE bookmark_id = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", bookmarkId);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? Read(reader) : null;
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
            return null;
        }
    }

    /// <summary>Delete one bookmark's row, returning it (null when absent or no table).</summary>
    public static async Task<ClipNarrationRecord?> DeleteForBookmarkAsync(SqliteConnection conn, SqliteTransaction? tx, long bookmarkId)
    {
        var existing = await ReadAsync(conn, tx, bookmarkId).ConfigureAwait(false);
        if (existing is null) return null;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM clip_narrations WHERE bookmark_id = @id";
        cmd.Parameters.AddWithValue("@id", bookmarkId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        return existing;
    }

    /// <summary>Delete every row of one game's bookmarks (by bookmark membership AND game id).</summary>
    public static async Task DeleteForGameBookmarksAsync(SqliteConnection conn, SqliteTransaction? tx, long gameId)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM clip_narrations
                WHERE game_id = @gameId
                   OR bookmark_id IN (SELECT id FROM vod_bookmarks WHERE game_id = @gameId)
                """;
            cmd.Parameters.AddWithValue("@gameId", gameId);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
        }
    }
}
