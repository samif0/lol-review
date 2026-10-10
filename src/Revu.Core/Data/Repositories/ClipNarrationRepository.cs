#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// 3.14 narrated clips: the clip_narrations table (see <see cref="IClipNarrationRepository"/>).
/// <para>
/// Missing-table tolerance mirrors VodRepository's share_url handling: a read on a
/// database without the table returns empty, a write creates the table (the same DDL
/// AllCreateStatements runs) and retries once. On the read-only graph only reads run.
/// </para>
/// <para>
/// updated_at tracks the NARRATION (voice track, render, mix), not the transcript: it
/// changes on upsert and re-mix only, so the transcription worker's settle window keys
/// on the user's last recording action.
/// </para>
/// </summary>
public sealed class ClipNarrationRepository : IClipNarrationRepository
{
    private readonly IDbConnectionFactory _factory;

    public ClipNarrationRepository(IDbConnectionFactory factory) => _factory = factory;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public Task<ClipNarrationRecord?> GetAsync(long bookmarkId) =>
        ReadAsync(conn => ClipNarrationSql.ReadAsync(conn, null, bookmarkId), null);

    public Task<Dictionary<long, ClipNarrationRecord>> GetForGameAsync(long gameId) =>
        ReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {ClipNarrationSql.Columns} FROM clip_narrations WHERE game_id = @gameId";
            cmd.Parameters.AddWithValue("@gameId", gameId);
            var rows = await ReadAllAsync(cmd).ConfigureAwait(false);
            return rows.ToDictionary(r => r.BookmarkId);
        }, new Dictionary<long, ClipNarrationRecord>());

    public Task<ClipNarrationRecord?> UpsertAsync(ClipNarrationRecord record) =>
        WriteAsync(async conn =>
        {
            using var tx = conn.BeginTransaction();
            var previous = await ClipNarrationSql.ReadAsync(conn, tx, record.BookmarkId).ConfigureAwait(false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                // A fresh row starts its generation at the current unix-ms time, so a row
                // re-created after a delete never reuses a generation an older run holds.
                cmd.CommandText = """
                    INSERT INTO clip_narrations (bookmark_id, game_id, narration_id, audio_path,
                        narrated_clip_path, source_clip_path, offset_ms, duration_ms, game_volume,
                        narration_volume, duck, transcript_status, transcript_generation,
                        transcript_language, transcript_json, transcript_error, transcript_pushed_slug,
                        created_at, updated_at)
                    VALUES (@b, @g, @nid, @audio, @narrated, @source, @offset, @duration, @gv, @nv,
                        @duck, @status, @gen, '', '', '', '', @now, @now)
                    ON CONFLICT(bookmark_id) DO UPDATE SET
                        game_id = excluded.game_id,
                        narration_id = excluded.narration_id,
                        audio_path = excluded.audio_path,
                        narrated_clip_path = excluded.narrated_clip_path,
                        source_clip_path = excluded.source_clip_path,
                        offset_ms = excluded.offset_ms,
                        duration_ms = excluded.duration_ms,
                        game_volume = excluded.game_volume,
                        narration_volume = excluded.narration_volume,
                        duck = excluded.duck,
                        transcript_status = excluded.transcript_status,
                        transcript_generation = clip_narrations.transcript_generation + 1,
                        transcript_language = '',
                        transcript_json = '',
                        transcript_error = '',
                        transcript_pushed_slug = '',
                        updated_at = excluded.updated_at
                    """;
                cmd.Parameters.AddWithValue("@b", record.BookmarkId);
                cmd.Parameters.AddWithValue("@g", record.GameId);
                cmd.Parameters.AddWithValue("@nid", record.NarrationId ?? "");
                cmd.Parameters.AddWithValue("@audio", record.AudioPath ?? "");
                cmd.Parameters.AddWithValue("@narrated", record.NarratedClipPath ?? "");
                cmd.Parameters.AddWithValue("@source", record.SourceClipPath ?? "");
                cmd.Parameters.AddWithValue("@offset", record.OffsetMs);
                cmd.Parameters.AddWithValue("@duration", record.DurationMs);
                cmd.Parameters.AddWithValue("@gv", record.GameVolume);
                cmd.Parameters.AddWithValue("@nv", record.NarrationVolume);
                cmd.Parameters.AddWithValue("@duck", record.Duck ? 1 : 0);
                cmd.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(record.TranscriptStatus)
                    ? TranscriptStatuses.Pending : record.TranscriptStatus);
                cmd.Parameters.AddWithValue("@gen", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@now", Now());
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
            return previous;
        });

    public Task<ClipNarrationRecord?> UpdateMixAsync(long bookmarkId, string narratedClipPath, int offsetMs,
        double gameVolume, double narrationVolume, bool duck, bool resetTranscript, string statusIfReset) =>
        WriteAsync(async conn =>
        {
            using var tx = conn.BeginTransaction();
            var previous = await ClipNarrationSql.ReadAsync(conn, tx, bookmarkId).ConfigureAwait(false);
            if (previous is null) return null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = resetTranscript
                    ? """
                      UPDATE clip_narrations SET narrated_clip_path = @p, offset_ms = @o, game_volume = @gv,
                          narration_volume = @nv, duck = @duck, updated_at = @now,
                          transcript_generation = transcript_generation + 1, transcript_status = @status,
                          transcript_language = '', transcript_json = '', transcript_error = '',
                          transcript_pushed_slug = ''
                      WHERE bookmark_id = @b
                      """
                    : """
                      UPDATE clip_narrations SET narrated_clip_path = @p, offset_ms = @o, game_volume = @gv,
                          narration_volume = @nv, duck = @duck, updated_at = @now
                      WHERE bookmark_id = @b
                      """;
                cmd.Parameters.AddWithValue("@p", narratedClipPath ?? "");
                cmd.Parameters.AddWithValue("@o", offsetMs);
                cmd.Parameters.AddWithValue("@gv", gameVolume);
                cmd.Parameters.AddWithValue("@nv", narrationVolume);
                cmd.Parameters.AddWithValue("@duck", duck ? 1 : 0);
                cmd.Parameters.AddWithValue("@now", Now());
                cmd.Parameters.AddWithValue("@status", statusIfReset ?? TranscriptStatuses.Pending);
                cmd.Parameters.AddWithValue("@b", bookmarkId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
            return previous;
        });

    public Task<bool> ResetTranscriptAsync(long bookmarkId, string status) =>
        WriteAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE clip_narrations SET transcript_generation = transcript_generation + 1,
                    transcript_status = @status, transcript_language = '', transcript_json = '',
                    transcript_error = '', transcript_pushed_slug = ''
                WHERE bookmark_id = @b
                """;
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@b", bookmarkId);
            return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
        });

    public Task<bool> TryClaimTranscriptAsync(long bookmarkId, long generation) =>
        WriteAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE clip_narrations SET transcript_status = 'processing'
                WHERE bookmark_id = @b AND transcript_generation = @g AND transcript_status = 'pending'
                """;
            cmd.Parameters.AddWithValue("@b", bookmarkId);
            cmd.Parameters.AddWithValue("@g", generation);
            return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
        });

    public Task<bool> TrySetTranscriptAsync(long bookmarkId, long generation, string status,
        string language, string json, string error) =>
        WriteAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE clip_narrations SET transcript_status = @status, transcript_language = @lang,
                    transcript_json = @json, transcript_error = @error
                WHERE bookmark_id = @b AND transcript_generation = @g AND transcript_status = 'processing'
                """;
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@lang", language ?? "");
            cmd.Parameters.AddWithValue("@json", json ?? "");
            cmd.Parameters.AddWithValue("@error", error ?? "");
            cmd.Parameters.AddWithValue("@b", bookmarkId);
            cmd.Parameters.AddWithValue("@g", generation);
            return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
        });

    public Task SetTranscriptPushedSlugAsync(long bookmarkId, string slug) =>
        WriteAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clip_narrations SET transcript_pushed_slug = @slug WHERE bookmark_id = @b";
            cmd.Parameters.AddWithValue("@slug", slug ?? "");
            cmd.Parameters.AddWithValue("@b", bookmarkId);
            return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        });

    public Task<ClipNarrationRecord?> DeleteAsync(long bookmarkId) =>
        WriteAsync(async conn =>
        {
            using var tx = conn.BeginTransaction();
            var deleted = await ClipNarrationSql.DeleteForBookmarkAsync(conn, tx, bookmarkId).ConfigureAwait(false);
            tx.Commit();
            return deleted;
        });

    public Task<IReadOnlyList<ClipNarrationRecord>> ListByTranscriptStatusAsync(params string[] statuses)
    {
        if (statuses is null || statuses.Length == 0)
            return Task.FromResult<IReadOnlyList<ClipNarrationRecord>>(Array.Empty<ClipNarrationRecord>());
        return ReadAsync<IReadOnlyList<ClipNarrationRecord>>(async conn =>
        {
            using var cmd = conn.CreateCommand();
            var names = new List<string>(statuses.Length);
            for (var i = 0; i < statuses.Length; i++)
            {
                names.Add($"@s{i}");
                cmd.Parameters.AddWithValue($"@s{i}", statuses[i] ?? "");
            }
            cmd.CommandText = $"""
                SELECT {ClipNarrationSql.Columns} FROM clip_narrations
                WHERE transcript_status IN ({string.Join(", ", names)})
                ORDER BY updated_at ASC, bookmark_id ASC
                """;
            return await ReadAllAsync(cmd).ConfigureAwait(false);
        }, Array.Empty<ClipNarrationRecord>());
    }

    public Task<int> ResetProcessingToPendingAsync() =>
        WriteAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clip_narrations SET transcript_status = 'pending' WHERE transcript_status = 'processing'";
            return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        });

    public Task<IReadOnlyList<string>> ListAudioPathsAsync() =>
        ReadAsync<IReadOnlyList<string>>(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT audio_path FROM clip_narrations WHERE audio_path != ''";
            return await ReadStringsAsync(cmd).ConfigureAwait(false);
        }, Array.Empty<string>());

    public Task<int> CountOrphansAsync() =>
        ReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM clip_narrations WHERE bookmark_id NOT IN (SELECT id FROM vod_bookmarks)";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }, 0);

    public Task<IReadOnlyList<ClipNarrationRecord>> DeleteOrphansAsync() =>
        WriteAsync<IReadOnlyList<ClipNarrationRecord>>(async conn =>
        {
            using var tx = conn.BeginTransaction();
            List<ClipNarrationRecord> orphans;
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = $"""
                    SELECT {ClipNarrationSql.Columns} FROM clip_narrations
                    WHERE bookmark_id NOT IN (SELECT id FROM vod_bookmarks)
                    """;
                orphans = await ReadAllAsync(select).ConfigureAwait(false);
            }
            foreach (var orphan in orphans)
            {
                using var delete = conn.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM clip_narrations WHERE bookmark_id = @b";
                delete.Parameters.AddWithValue("@b", orphan.BookmarkId);
                await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
            return orphans;
        });

    public Task<IReadOnlyList<string>> ListProtectedClipPathsAsync() =>
        ReadAsync<IReadOnlyList<string>>(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT source_clip_path FROM clip_narrations WHERE source_clip_path != ''
                UNION
                SELECT narrated_clip_path FROM clip_narrations WHERE narrated_clip_path != ''
                """;
            return await ReadStringsAsync(cmd).ConfigureAwait(false);
        }, Array.Empty<string>());

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> read, T whenMissing)
    {
        using var conn = _factory.CreateConnection();
        try
        {
            return await read(conn).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ClipNarrationSql.IsMissingTable(ex))
        {
            return whenMissing;
        }
    }

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, Task<T>> write)
    {
        using var conn = _factory.CreateConnection();
        try
        {
            return await write(conn).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ClipNarrationSql.IsMissingTable(ex))
        {
            await ClipNarrationSql.EnsureTableAsync(conn).ConfigureAwait(false);
            return await write(conn).ConfigureAwait(false);
        }
    }

    private static async Task<List<ClipNarrationRecord>> ReadAllAsync(SqliteCommand cmd)
    {
        var rows = new List<ClipNarrationRecord>();
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false)) rows.Add(ClipNarrationSql.Read(reader));
        return rows;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteCommand cmd)
    {
        var values = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0) && reader.GetString(0).Length > 0) values.Add(reader.GetString(0));
        }
        return values;
    }
}
