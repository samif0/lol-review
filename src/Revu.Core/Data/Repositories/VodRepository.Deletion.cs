#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Bookmark / clip deletion. Foreign keys are off, so every path here also removes the
/// bookmark's clip_narrations row explicitly (3.14), inside the same transaction.
/// </summary>
public sealed partial class VodRepository
{
    public async Task DeleteBookmarkAsync(long bookmarkId)
    {
        // Note-bookmark delete: the DB rows only. Clip and narration FILES are not touched
        // here (this path never deleted clip files); unreferenced narration files are
        // removed by the sidecar's startup sweep.
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await ClipNarrationSql.DeleteForBookmarkAsync(conn, tx, bookmarkId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM vod_bookmarks WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", bookmarkId);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<ClipDeletionInfo?> DeleteClipFullAsync(long bookmarkId)
    {
        using var conn = _factory.CreateConnection();

        // Read the on-disk path + share URL BEFORE deleting — the caller needs them to
        // delete the file and the uploaded copy, which live outside the DB.
        string clipPath = "";
        string shareUrl = "";
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT clip_path, share_url FROM vod_bookmarks WHERE id = @id LIMIT 1";
            read.Parameters.AddWithValue("@id", bookmarkId);
            using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null; // no such bookmark
            clipPath = reader.IsDBNull(0) ? "" : reader.GetString(0);
            shareUrl = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        ClipNarrationRecord? narration;
        using var tx = conn.BeginTransaction();
        try
        {
            // Drop any evidence ledger row that pointed at this clip (so it doesn't
            // linger as a dangling "clip" entry on the objective). source_id holds the
            // bookmark id for source_kind='clip'.
            using (var ev = conn.CreateCommand())
            {
                ev.Transaction = tx;
                ev.CommandText = "DELETE FROM evidence_items WHERE source_kind = @kind AND source_id = @id";
                ev.Parameters.AddWithValue("@kind", EvidenceKinds.Clip);
                ev.Parameters.AddWithValue("@id", bookmarkId);
                await ev.ExecuteNonQueryAsync();
            }

            // 3.14: the narration row goes with the clip; its file paths go back to the
            // caller (voice track + narrated render live outside the DB).
            narration = await ClipNarrationSql.DeleteForBookmarkAsync(conn, tx, bookmarkId);

            using (var bm = conn.CreateCommand())
            {
                bm.Transaction = tx;
                bm.CommandText = "DELETE FROM vod_bookmarks WHERE id = @id";
                bm.Parameters.AddWithValue("@id", bookmarkId);
                await bm.ExecuteNonQueryAsync();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return new ClipDeletionInfo(clipPath, shareUrl,
            NarrationAudioPath: narration?.AudioPath ?? "",
            NarratedClipPath: narration?.NarratedClipPath ?? "");
    }

    public async Task DeleteAllBookmarksAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await ClipNarrationSql.DeleteForGameBookmarksAsync(conn, tx, gameId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM vod_bookmarks WHERE game_id = @gameId";
                cmd.Parameters.AddWithValue("@gameId", gameId);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListSharedClipPathsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT clip_path FROM vod_bookmarks
            WHERE clip_path IS NOT NULL AND clip_path != ''
              AND share_url IS NOT NULL AND share_url != ''
            """;
        var paths = new List<string>();
        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0)) paths.Add(reader.GetString(0));
            }
        }
        catch (SqliteException ex) when (IsMissingColumn(ex, "share_url"))
        {
            // A DB without share_url has no shared clips.
        }
        return paths;
    }
}
